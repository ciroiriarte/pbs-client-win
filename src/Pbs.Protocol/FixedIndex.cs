using System.Buffers.Binary;
using System.Security.Cryptography;
using Pbs.Abstractions;

namespace Pbs.Protocol;

/// <summary>
/// Reader/writer for the PBS fixed-size chunk index (<c>.fidx</c>) — used for block/image
/// archives. Header is 4096 bytes:
/// <c>magic[8] | uuid[16] | ctime:i64-le[8] | index_csum[32] | size:u64-le[8] | chunk_size:u64-le[8] | reserved[4016]</c>,
/// followed by one 32-byte SHA-256 digest per chunk. <c>index_csum = SHA256(digest0 || digest1 || …)</c>.
/// </summary>
public static class FixedIndex
{
    private const int MagicOff = 0;
    private const int UuidOff = 8;
    private const int CtimeOff = 24;
    private const int CsumOff = 32;
    private const int SizeOff = 64;
    private const int ChunkSizeOff = 72;

    /// <summary>Serialize a fixed index for <paramref name="digests"/> covering <paramref name="size"/> bytes.</summary>
    public static byte[] Build(long size, long chunkSize, IReadOnlyList<ChunkDigest> digests, long ctimeSeconds, ReadOnlySpan<byte> uuid)
    {
        ArgumentNullException.ThrowIfNull(digests);
        if (uuid.Length != 16) throw new ArgumentException("uuid must be 16 bytes", nameof(uuid));

        long expected = chunkSize == 0 ? 0 : (size + chunkSize - 1) / chunkSize;
        if (digests.Count != expected)
            throw new ArgumentException($"expected {expected} digests for size {size}/chunk {chunkSize}, got {digests.Count}");

        var buffer = new byte[FileFormats.IndexHeaderSize + digests.Count * ChunkDigest.Size];
        FileFormats.FixedSizedChunkIndexMagic.CopyTo(buffer.AsSpan(MagicOff, 8));
        uuid.CopyTo(buffer.AsSpan(UuidOff, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(CtimeOff, 8), ctimeSeconds);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(SizeOff, 8), (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(ChunkSizeOff, 8), (ulong)chunkSize);

        int pos = FileFormats.IndexHeaderSize;
        using var csum = SHA256.Create();
        foreach (var d in digests)
        {
            d.Span.CopyTo(buffer.AsSpan(pos, ChunkDigest.Size));
            csum.TransformBlock(buffer, pos, ChunkDigest.Size, null, 0);
            pos += ChunkDigest.Size;
        }
        csum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        csum.Hash!.CopyTo(buffer.AsSpan(CsumOff, 32));

        return buffer;
    }

    /// <summary>
    /// Compute the <c>index_csum</c> for a fixed index: SHA256 over the concatenated chunk digests.
    /// This is what PBS expects in <c>fixed_close</c> and recomputes during verify.
    /// </summary>
    public static ChunkDigest ComputeCsum(IReadOnlyList<ChunkDigest> digests)
    {
        ArgumentNullException.ThrowIfNull(digests);
        using var csum = SHA256.Create();
        foreach (var d in digests)
        {
            var tmp = d.Span.ToArray();
            csum.TransformBlock(tmp, 0, tmp.Length, null, 0);
        }
        csum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return ChunkDigest.FromBytes(csum.Hash!);
    }

    /// <summary>Parse a fixed index, validating magic and recomputing <c>index_csum</c>.</summary>
    public static (long Size, long ChunkSize, IReadOnlyList<ChunkDigest> Digests) Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileFormats.IndexHeaderSize)
            throw new InvalidDataException("fixed index shorter than header");
        if (!data.Slice(MagicOff, 8).SequenceEqual(FileFormats.FixedSizedChunkIndexMagic))
            throw new InvalidDataException("not a fixed index (bad magic)");

        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(SizeOff, 8));
        long chunkSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ChunkSizeOff, 8));
        var storedCsum = data.Slice(CsumOff, 32);

        int bodyLen = data.Length - FileFormats.IndexHeaderSize;
        if (bodyLen % ChunkDigest.Size != 0)
            throw new InvalidDataException("fixed index body not a multiple of 32");
        int count = bodyLen / ChunkDigest.Size;

        var digests = new List<ChunkDigest>(count);
        using var csum = SHA256.Create();
        for (int i = 0; i < count; i++)
        {
            int off = FileFormats.IndexHeaderSize + i * ChunkDigest.Size;
            var slice = data.Slice(off, ChunkDigest.Size);
            digests.Add(ChunkDigest.FromBytes(slice));
            var tmp = slice.ToArray();
            csum.TransformBlock(tmp, 0, tmp.Length, null, 0);
        }
        csum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (!csum.Hash!.AsSpan().SequenceEqual(storedCsum))
            throw new InvalidDataException("fixed index csum mismatch");

        return (size, chunkSize, digests);
    }
}
