using System.Buffers.Binary;
using System.Security.Cryptography;
using Pbs.Abstractions;

namespace Pbs.Protocol;

/// <summary>One dynamic-index entry: the exclusive end offset of a chunk and its digest.</summary>
public readonly record struct DynamicIndexEntry(long EndOffset, ChunkDigest Digest);

/// <summary>
/// Reader/writer for the PBS dynamic (content-defined) chunk index (<c>.didx</c>) — used for
/// file/stream archives. Header is 4096 bytes:
/// <c>magic[8] | uuid[16] | ctime:i64-le[8] | index_csum[32] | reserved[4032]</c>, followed by
/// 40-byte entries: <c>end_offset:u64-le[8] | digest[32]</c>.
/// <c>index_csum = SHA256( for each entry: end_offset_le || digest )</c>.
/// </summary>
public static class DynamicIndex
{
    private const int MagicOff = 0;
    private const int UuidOff = 8;
    private const int CtimeOff = 24;
    private const int CsumOff = 32;
    private const int EntrySize = 40;

    /// <summary>Build a dynamic index from ordered chunk references (offsets must be contiguous).</summary>
    public static byte[] Build(IReadOnlyList<ChunkRef> chunks, long ctimeSeconds, ReadOnlySpan<byte> uuid)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (uuid.Length != 16) throw new ArgumentException("uuid must be 16 bytes", nameof(uuid));

        var buffer = new byte[FileFormats.IndexHeaderSize + chunks.Count * EntrySize];
        FileFormats.DynamicSizedChunkIndexMagic.CopyTo(buffer.AsSpan(MagicOff, 8));
        uuid.CopyTo(buffer.AsSpan(UuidOff, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(CtimeOff, 8), ctimeSeconds);

        int pos = FileFormats.IndexHeaderSize;
        Span<byte> endBytes = stackalloc byte[8];
        using var csum = SHA256.Create();
        foreach (var c in chunks)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos, 8), (ulong)c.End);
            c.Digest.Span.CopyTo(buffer.AsSpan(pos + 8, ChunkDigest.Size));

            BinaryPrimitives.WriteUInt64LittleEndian(endBytes, (ulong)c.End);
            var block = new byte[EntrySize];
            endBytes.CopyTo(block);
            c.Digest.Span.CopyTo(block.AsSpan(8));
            csum.TransformBlock(block, 0, block.Length, null, 0);

            pos += EntrySize;
        }
        csum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        csum.Hash!.CopyTo(buffer.AsSpan(CsumOff, 32));

        return buffer;
    }

    /// <summary>Parse a dynamic index, validating magic and recomputing <c>index_csum</c>.</summary>
    public static IReadOnlyList<DynamicIndexEntry> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileFormats.IndexHeaderSize)
            throw new InvalidDataException("dynamic index shorter than header");
        if (!data.Slice(MagicOff, 8).SequenceEqual(FileFormats.DynamicSizedChunkIndexMagic))
            throw new InvalidDataException("not a dynamic index (bad magic)");

        var storedCsum = data.Slice(CsumOff, 32);
        int bodyLen = data.Length - FileFormats.IndexHeaderSize;
        if (bodyLen % EntrySize != 0)
            throw new InvalidDataException("dynamic index body not a multiple of 40");
        int count = bodyLen / EntrySize;

        var entries = new List<DynamicIndexEntry>(count);
        using var csum = SHA256.Create();
        for (int i = 0; i < count; i++)
        {
            int off = FileFormats.IndexHeaderSize + i * EntrySize;
            long end = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(off, 8));
            var digest = ChunkDigest.FromBytes(data.Slice(off + 8, ChunkDigest.Size));
            entries.Add(new DynamicIndexEntry(end, digest));

            var block = data.Slice(off, EntrySize).ToArray();
            csum.TransformBlock(block, 0, block.Length, null, 0);
        }
        csum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (!csum.Hash!.AsSpan().SequenceEqual(storedCsum))
            throw new InvalidDataException("dynamic index csum mismatch");

        return entries;
    }
}
