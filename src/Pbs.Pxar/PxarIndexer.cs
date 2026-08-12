using System.Buffers.Binary;
using System.Text;

namespace Pbs.Pxar;

public enum PxarEntryKind
{
    Directory,
    File,
    Symlink,
    /// <summary>Device, FIFO, socket, or hardlink — present in the tree but with no local payload.</summary>
    Special,
}

public sealed record PxarEntryLocation(
    string RelativePath,
    PxarEntryKind Kind,
    long PayloadOffset,
    long PayloadLength,
    uint Mode,
    ulong MtimeSecs);

/// <summary>
/// Builds a seek-friendly index over a pxar archive without reading file payload bytes.
/// Interoperable with archives produced by the official proxmox-backup-client: after each ENTRY it
/// skips any non-structural record (XATTR / ACL / FCAPS / QUOTA / future/unknown records) before the
/// real content, and dispatches on the stat mode so directories, files, symlinks, and special files
/// (device / FIFO / socket / hardlink) are all handled.
/// </summary>
public sealed class PxarIndexer
{
    private const int HeaderSize = PxarFormat.HeaderSize;

    private const ulong ModeFmt = 0xF000;
    private const ulong ModeDir = 0x4000;   // S_IFDIR
    private const ulong ModeReg = 0x8000;   // S_IFREG
    private const ulong ModeLnk = 0xA000;   // S_IFLNK

    private readonly Stream _in;
    private readonly List<PxarEntryLocation> _entries = new();

    private PxarIndexer(Stream input) => _in = input;

    public static async Task<IReadOnlyList<PxarEntryLocation>> BuildIndexAsync(Stream pxar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pxar);
        if (!pxar.CanSeek)
            throw new ArgumentException("pxar index requires a seekable stream", nameof(pxar));
        if (!pxar.CanRead)
            throw new ArgumentException("pxar index requires a readable stream", nameof(pxar));

        pxar.Position = 0;
        var indexer = new PxarIndexer(pxar);

        // Skip any leading non-ENTRY records (FORMAT_VERSION, PRELUDE, or future additions) until the
        // root ENTRY. The official client may emit records our writer does not.
        ulong htype;
        long size;
        while (true)
        {
            (htype, size) = await indexer.ReadHeaderAsync(ct);
            if (htype == PxarFormat.Entry)
                break;
            await indexer.SkipAsync(size - HeaderSize, ct);
        }

        var rootStat = await indexer.ReadStatAsync(size, ct);
        if ((rootStat.Mode & ModeFmt) != ModeDir)
            throw new InvalidDataException("pxar: root ENTRY is not a directory");

        await indexer.ScanEntryBodyAsync(string.Empty, rootStat, ct);
        return indexer._entries;
    }

    // A directory body is (FILENAME + ENTRY-body)* terminated by GOODBYE.
    private async Task ScanDirBodyAsync(string relativePrefix, CancellationToken ct)
    {
        while (true)
        {
            var (htype, size) = await ReadHeaderAsync(ct);
            if (htype == PxarFormat.Goodbye)
            {
                await SkipAsync(size - HeaderSize, ct);
                return;
            }
            if (htype != PxarFormat.Filename)
            {
                // Defensive forward-compat: skip anything unexpected between children.
                await SkipAsync(size - HeaderSize, ct);
                continue;
            }

            var name = await ReadNameAsync(size, ct);
            var relPath = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";

            var (entryType, entrySize) = await ReadHeaderAsync(ct);
            if (entryType != PxarFormat.Entry)
                throw new InvalidDataException($"pxar: expected ENTRY after FILENAME, got htype {entryType:x16}");

            var stat = await ReadStatAsync(entrySize, ct);
            await ScanEntryBodyAsync(relPath, stat, ct);
        }
    }

    // After an ENTRY's stat has been read: consume this entry's metadata records, then its content
    // (dir body, PAYLOAD, SYMLINK, …) based on the stat mode. Records the entry's location.
    private async Task ScanEntryBodyAsync(string relPath, EntryStat stat, CancellationToken ct)
    {
        var modeType = stat.Mode & ModeFmt;

        if (modeType == ModeDir)
        {
            _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.Directory, 0, 0, (uint)stat.Mode, stat.MtimeSecs));
            // Skip the directory's own metadata records; the body starts at the first FILENAME/GOODBYE.
            while (true)
            {
                var startPos = _in.Position;
                var (htype, size) = await ReadHeaderAsync(ct);
                if (htype == PxarFormat.Filename || htype == PxarFormat.Goodbye)
                {
                    _in.Position = startPos;
                    await ScanDirBodyAsync(relPath, ct);
                    return;
                }
                await SkipAsync(size - HeaderSize, ct);
            }
        }

        // Non-directory: skip metadata records, then act on the first structural record.
        while (true)
        {
            var startPos = _in.Position;
            var (htype, size) = await ReadHeaderAsync(ct);
            if (!IsStructural(htype))
            {
                await SkipAsync(size - HeaderSize, ct);
                continue;
            }

            var payloadLen = size - HeaderSize;
            switch (modeType)
            {
                case ModeReg when htype == PxarFormat.Payload:
                    var payloadOffset = _in.Position;
                    _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.File, payloadOffset, payloadLen, (uint)stat.Mode, stat.MtimeSecs));
                    await SkipAsync(payloadLen, ct);
                    return;

                case ModeReg when htype == PxarFormat.Hardlink:
                    // A hardlink carries a target reference, not its own payload.
                    _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.Special, 0, 0, (uint)stat.Mode, stat.MtimeSecs));
                    await SkipAsync(payloadLen, ct);
                    return;

                case ModeLnk:
                    _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.Symlink, 0, 0, (uint)stat.Mode, stat.MtimeSecs));
                    if (htype == PxarFormat.Filename || htype == PxarFormat.Goodbye)
                        _in.Position = startPos;   // belongs to the parent directory
                    else
                        await SkipAsync(payloadLen, ct);
                    return;

                default:
                    // Regular file with no PAYLOAD (unexpected), or a special file (device/FIFO/socket).
                    var kind = modeType == ModeReg ? PxarEntryKind.File : PxarEntryKind.Special;
                    _entries.Add(new PxarEntryLocation(relPath, kind, 0, 0, (uint)stat.Mode, stat.MtimeSecs));
                    if (htype == PxarFormat.Filename || htype == PxarFormat.Goodbye)
                        _in.Position = startPos;   // parent's record; not this entry's content
                    else
                        await SkipAsync(payloadLen, ct);
                    return;
            }
        }
    }

    // Records that define archive structure (vs. per-entry metadata like XATTR/ACL/FCAPS/QUOTA).
    private static bool IsStructural(ulong htype) =>
        htype == PxarFormat.Filename || htype == PxarFormat.Goodbye || htype == PxarFormat.Payload ||
        htype == PxarFormat.Symlink || htype == PxarFormat.Device || htype == PxarFormat.Hardlink;

    private async Task<(ulong htype, long size)> ReadHeaderAsync(CancellationToken ct)
    {
        var buffer = new byte[HeaderSize];
        await ReadExactAsync(buffer, ct);
        ulong htype = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        if (size < HeaderSize) throw new InvalidDataException("pxar: header size underflow");
        return (htype, size);
    }

    private async Task<EntryStat> ReadStatAsync(long entryRecordSize, CancellationToken ct)
    {
        if (entryRecordSize < HeaderSize + PxarFormat.StatSize)
            throw new InvalidDataException("pxar: ENTRY stat underflow");

        var buffer = new byte[PxarFormat.StatSize];
        await ReadExactAsync(buffer, ct);
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        var mtime = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24, 8));
        await SkipAsync(entryRecordSize - HeaderSize - PxarFormat.StatSize, ct);
        return new EntryStat(mode, mtime < 0 ? 0 : (ulong)mtime);
    }

    private async Task<string> ReadNameAsync(long headerSize, CancellationToken ct)
    {
        var name = await ReadStringAsync(headerSize, ct);
        if (name.Contains('/') || name.Contains('\\') || name == ".." || name == ".")
            throw new InvalidDataException($"pxar: unsafe entry name '{name}'");
        return name;
    }

    private async Task<string> ReadStringAsync(long headerSize, CancellationToken ct)
    {
        int len = checked((int)(headerSize - HeaderSize));
        var buffer = new byte[len];
        await ReadExactAsync(buffer, ct);
        int end = len > 0 && buffer[len - 1] == 0 ? len - 1 : len;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    private Task SkipAsync(long count, CancellationToken ct)
    {
        if (count < 0)
            throw new InvalidDataException("pxar: negative skip");
        ct.ThrowIfCancellationRequested();
        var next = _in.Position + count;
        if (next > _in.Length)
            throw new EndOfStreamException("pxar: truncated while skipping");
        _in.Position = next;
        return Task.CompletedTask;
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _in.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("pxar: unexpected end of archive");
            read += n;
        }
    }

    private readonly record struct EntryStat(ulong Mode, ulong MtimeSecs);
}
