using System.Buffers.Binary;
using System.IO.Hashing;
using ZstdSharp;

namespace Pbs.Protocol;

/// <summary>
/// A Proxmox Backup "DataBlob" — the framing PBS uses for every chunk and blob it stores.
/// Layout (unencrypted): <c>magic[8] | crc32-le[4] | payload</c>, where the CRC32 (IEEE/zlib,
/// same as upstream <c>crc32fast</c>) covers only the payload bytes after the 12-byte header.
/// The payload is either the raw data (uncompressed magic) or a zstd stream (compressed magic).
/// </summary>
/// <remarks>
/// Encrypted blobs (AES-256-GCM, header <c>magic[8] | crc[4] | iv[16] | tag[16]</c>) are M2 and
/// intentionally not implemented here yet — <see cref="Encode"/> throws if asked to encrypt.
/// </remarks>
public static class DataBlob
{
    private const int HeaderSize = 12; // magic[8] + crc[4]

    /// <summary>
    /// Encode <paramref name="data"/> as a DataBlob. When <paramref name="compress"/> is true the
    /// payload is zstd-compressed and the compressed magic is used only if it is actually smaller.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, bool compress = true, int compressionLevel = 1)
    {
        byte[] payload;
        byte[] magic;

        if (compress)
        {
            using var compressor = new Compressor(compressionLevel);
            var compressed = compressor.Wrap(data.ToArray()).ToArray();
            if (compressed.Length < data.Length)
            {
                payload = compressed;
                magic = FileFormats.CompressedBlobMagic;
            }
            else
            {
                payload = data.ToArray();
                magic = FileFormats.UncompressedBlobMagic;
            }
        }
        else
        {
            payload = data.ToArray();
            magic = FileFormats.UncompressedBlobMagic;
        }

        var blob = new byte[HeaderSize + payload.Length];
        magic.CopyTo(blob.AsSpan(0, 8));
        Buffer.BlockCopy(payload, 0, blob, HeaderSize, payload.Length);

        uint crc = Crc32.HashToUInt32(blob.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8, 4), crc);

        return blob;
    }

    /// <summary>Decode a DataBlob back to its plaintext, verifying the CRC32 and decompressing.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize)
            throw new InvalidDataException("DataBlob shorter than its header.");

        var magic = blob[..8];
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8, 4));
        var payload = blob[HeaderSize..];

        uint actualCrc = Crc32.HashToUInt32(payload);
        if (actualCrc != storedCrc)
            throw new InvalidDataException($"DataBlob CRC mismatch: stored {storedCrc:x8}, computed {actualCrc:x8}.");

        if (magic.SequenceEqual(FileFormats.UncompressedBlobMagic))
            return payload.ToArray();

        if (magic.SequenceEqual(FileFormats.CompressedBlobMagic))
        {
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(payload.ToArray()).ToArray();
        }

        if (magic.SequenceEqual(FileFormats.EncryptedBlobMagic) ||
            magic.SequenceEqual(FileFormats.EncrComprBlobMagic))
            throw new NotSupportedException("Encrypted DataBlob decoding is not implemented yet (M2).");

        throw new InvalidDataException("Unknown DataBlob magic.");
    }
}
