using System.Buffers.Binary;
using System.Text;

namespace Pbs.Pxar;

/// <summary>
/// Encodes a <see cref="PxarNode"/> tree into a pxar archive stream. Layout per directory:
/// <c>ENTRY</c> (Stat) → for each child <c>FILENAME</c> then the child's content (recursively) →
/// a <c>GOODBYE</c> table. Files carry <c>ENTRY</c> + <c>PAYLOAD</c>; symlinks <c>ENTRY</c> +
/// <c>SYMLINK</c>. Offsets/sizes for the goodbye table are tracked as the stream is written.
/// </summary>
public sealed class PxarEncoder
{
    private readonly Stream _out;
    private long _pos;

    private PxarEncoder(Stream output) => _out = output;

    /// <summary>Encode <paramref name="root"/> (a directory) as a pxar archive into <paramref name="output"/>.</summary>
    public static async Task EncodeAsync(PxarDirNode root, Stream output, bool writeFormatVersion = true, CancellationToken ct = default)
    {
        var enc = new PxarEncoder(output);
        if (writeFormatVersion)
            await enc.WriteAsync(BuildFormatVersion(2), ct);
        await enc.EncodeNodeAsync(root, ct);
    }

    private async Task EncodeNodeAsync(PxarNode node, CancellationToken ct)
    {
        long entryStart = _pos;
        await WriteAsync(BuildEntry(node), ct);

        switch (node)
        {
            case PxarFileNode file:
                await WritePayloadAsync(file, ct);
                break;
            case PxarSymlinkNode link:
                await WriteAsync(BuildNullTerminated(PxarFormat.Symlink, link.Target), ct);
                break;
            case PxarDirNode dir:
                await WriteDirectoryBodyAsync(dir, entryStart, ct);
                break;
            default:
                throw new NotSupportedException(node.GetType().Name);
        }
    }

    private async Task WriteDirectoryBodyAsync(PxarDirNode dir, long entryStart, CancellationToken ct)
    {
        // pxar orders directory entries by their raw name bytes.
        var children = dir.Children
            .OrderBy(c => Encoding.UTF8.GetBytes(c.Name), ByteArrayComparer.Instance)
            .ToList();

        var items = new List<GoodbyeItem>(children.Count);
        foreach (var child in children)
        {
            long childStart = _pos;
            await WriteAsync(BuildNullTerminated(PxarFormat.Filename, child.Name), ct);
            await EncodeNodeAsync(child, ct);
            var nameBytes = Encoding.UTF8.GetBytes(child.Name);
            items.Add(new GoodbyeItem(PxarFormat.HashFilename(nameBytes), childStart, _pos - childStart));
        }

        long goodbyeStart = _pos;
        await WriteAsync(BuildGoodbye(items, entryStart, goodbyeStart), ct);
    }

    private static byte[] BuildGoodbye(List<GoodbyeItem> items, long entryStart, long goodbyeStart)
    {
        int n = items.Count;
        long fullSize = PxarFormat.HeaderSize + (long)(n + 1) * PxarFormat.GoodbyeItemSize;

        // Hash-sort then permute into the BST layout the decoder bisects over.
        var sorted = items.OrderBy(it => it.Hash).ToArray();
        var ordered = new GoodbyeItem[n];
        BinaryTreeArray.Copy(n, (src, dst) => ordered[dst] = sorted[src]);

        var buffer = new byte[fullSize];
        WriteHeaderInto(buffer, PxarFormat.Goodbye, fullSize);
        int off = PxarFormat.HeaderSize;
        foreach (var it in ordered)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(off, 8), it.Hash);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(off + 8, 8), (ulong)(goodbyeStart - it.Offset));
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(off + 16, 8), (ulong)it.Size);
            off += PxarFormat.GoodbyeItemSize;
        }
        // Tail marker: points back to this directory's ENTRY; size = whole goodbye table size.
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(off, 8), PxarFormat.GoodbyeTailMarker);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(off + 8, 8), (ulong)(goodbyeStart - entryStart));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(off + 16, 8), (ulong)fullSize);
        return buffer;
    }

    private static byte[] BuildEntry(PxarNode node)
    {
        ulong mode = node switch
        {
            PxarDirNode => PxarFormat.ModeDir,
            PxarSymlinkNode => PxarFormat.ModeSymlink,
            _ => PxarFormat.ModeFile,
        };

        var buffer = new byte[PxarFormat.HeaderSize + PxarFormat.StatSize];
        WriteHeaderInto(buffer, PxarFormat.Entry, buffer.Length);
        var s = buffer.AsSpan(PxarFormat.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0, 8), mode);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(8, 8), 0);   // flags
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16, 4), 0);  // uid
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20, 4), 0);  // gid
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(24, 8), node.MtimeSecs);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(32, 4), node.MtimeNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(36, 4), 0);  // padding
        return buffer;
    }

    private async Task WritePayloadAsync(PxarFileNode file, CancellationToken ct)
    {
        await WriteAsync(BuildPayloadHeader(file.Length), ct);

        await using var src = file.OpenRead();
        var buffer = new byte[81920];
        long remaining = file.Length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await src.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) throw new EndOfStreamException($"file '{file.Name}' shorter than reported length");
            await WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    private static byte[] BuildPayloadHeader(long payloadLength)
    {
        var header = new byte[PxarFormat.HeaderSize];
        WriteHeaderInto(header, PxarFormat.Payload, PxarFormat.HeaderSize + payloadLength);
        return header;
    }

    private static byte[] BuildNullTerminated(ulong htype, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var buffer = new byte[PxarFormat.HeaderSize + bytes.Length + 1];
        WriteHeaderInto(buffer, htype, buffer.Length);
        bytes.CopyTo(buffer.AsSpan(PxarFormat.HeaderSize));
        buffer[^1] = 0;
        return buffer;
    }

    private static byte[] BuildFormatVersion(ulong version)
    {
        var buffer = new byte[PxarFormat.HeaderSize + 8];
        WriteHeaderInto(buffer, PxarFormat.FormatVersion, buffer.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(PxarFormat.HeaderSize, 8), version);
        return buffer;
    }

    private static void WriteHeaderInto(Span<byte> buffer, ulong htype, long fullSize)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0, 8), htype);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(8, 8), (ulong)fullSize);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _out.WriteAsync(data, ct);
        _pos += data.Length;
    }

    private readonly record struct GoodbyeItem(ulong Hash, long Offset, long Size);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
