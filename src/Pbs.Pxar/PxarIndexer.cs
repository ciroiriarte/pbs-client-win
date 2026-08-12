using System.Buffers.Binary;
using System.Text;

namespace Pbs.Pxar;

public enum PxarEntryKind
{
    Directory,
    File,
    Symlink,
}

public sealed record PxarEntryLocation(
    string RelativePath,
    PxarEntryKind Kind,
    long PayloadOffset,
    long PayloadLength,
    uint Mode,
    ulong MtimeSecs);

/// <summary>
/// Builds a seek-friendly index over a pxar stream without reading file payload bytes.
/// </summary>
public sealed class PxarIndexer
{
    private const ulong ModeFmt = 0xF000;
    private const ulong ModeDir = 0x4000;
    private const ulong ModeLnk = 0xA000;

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

        var (htype, size) = await indexer.ReadHeaderAsync(ct);
        if (htype == PxarFormat.FormatVersion)
        {
            await indexer.SkipAsync(size - PxarFormat.HeaderSize, ct);
            (htype, size) = await indexer.ReadHeaderAsync(ct);
        }

        if (htype != PxarFormat.Entry)
            throw new InvalidDataException($"pxar: expected root ENTRY, got htype {htype:x16}");

        var rootStat = await indexer.ReadStatAsync(size, ct);
        if ((rootStat.Mode & ModeFmt) != ModeDir)
            throw new InvalidDataException("pxar: root ENTRY is not a directory");

        indexer._entries.Add(new PxarEntryLocation(string.Empty, PxarEntryKind.Directory, 0, 0, (uint)rootStat.Mode, rootStat.MtimeSecs));
        await indexer.ScanDirBodyAsync(string.Empty, ct);
        return indexer._entries;
    }

    private async Task ScanDirBodyAsync(string relativePrefix, CancellationToken ct)
    {
        while (true)
        {
            var (htype, size) = await ReadHeaderAsync(ct);
            if (htype == PxarFormat.Goodbye)
            {
                await SkipAsync(size - PxarFormat.HeaderSize, ct);
                return;
            }
            if (htype != PxarFormat.Filename)
                throw new InvalidDataException($"pxar: expected FILENAME, got htype {htype:x16}");

            var name = await ReadNameAsync(size, ct);
            var relPath = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";

            var (entryType, entrySize) = await ReadHeaderAsync(ct);
            if (entryType != PxarFormat.Entry)
                throw new InvalidDataException($"pxar: expected ENTRY after FILENAME, got htype {entryType:x16}");

            var stat = await ReadStatAsync(entrySize, ct);
            var modeType = stat.Mode & ModeFmt;
            if (modeType == ModeDir)
            {
                _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.Directory, 0, 0, (uint)stat.Mode, stat.MtimeSecs));
                await ScanDirBodyAsync(relPath, ct);
            }
            else if (modeType == ModeLnk)
            {
                var (linkType, linkSize) = await ReadHeaderAsync(ct);
                if (linkType != PxarFormat.Symlink)
                    throw new InvalidDataException($"pxar: expected SYMLINK, got htype {linkType:x16}");

                await ReadStringAsync(linkSize, ct);
                _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.Symlink, 0, 0, (uint)stat.Mode, stat.MtimeSecs));
            }
            else
            {
                var (payloadType, payloadSize) = await ReadHeaderAsync(ct);
                if (payloadType != PxarFormat.Payload)
                    throw new InvalidDataException($"pxar: expected PAYLOAD, got htype {payloadType:x16}");

                var payloadLength = payloadSize - PxarFormat.HeaderSize;
                var payloadOffset = _in.Position;
                _entries.Add(new PxarEntryLocation(relPath, PxarEntryKind.File, payloadOffset, payloadLength, (uint)stat.Mode, stat.MtimeSecs));
                await SkipAsync(payloadLength, ct);
            }
        }
    }

    private async Task<(ulong htype, long size)> ReadHeaderAsync(CancellationToken ct)
    {
        var buffer = new byte[PxarFormat.HeaderSize];
        await ReadExactAsync(buffer, ct);
        ulong htype = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        if (size < PxarFormat.HeaderSize) throw new InvalidDataException("pxar: header size underflow");
        return (htype, size);
    }

    private async Task<EntryStat> ReadStatAsync(long entryRecordSize, CancellationToken ct)
    {
        if (entryRecordSize < PxarFormat.HeaderSize + PxarFormat.StatSize)
            throw new InvalidDataException("pxar: ENTRY stat underflow");

        var buffer = new byte[PxarFormat.StatSize];
        await ReadExactAsync(buffer, ct);
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        var mtime = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24, 8));
        await SkipAsync(entryRecordSize - PxarFormat.HeaderSize - PxarFormat.StatSize, ct);
        return new EntryStat(mode, checked((ulong)mtime));
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
        int len = checked((int)(headerSize - PxarFormat.HeaderSize));
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
