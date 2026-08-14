using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using ZstdSharp;

namespace Pbs.Protocol;

/// <summary>
/// PBS client-side encryption, matching upstream <c>pbs-tools/src/crypt_config.rs</c>. Backups
/// encrypted with a given 32-byte key are byte-compatible with the official client's
/// <c>--keyfile</c>: chunks are AES-256-GCM with a 16-byte IV, chunk digests are
/// <c>SHA256(plaintext ‖ id_key)</c>, and the key fingerprint is <c>SHA256(FINGERPRINT_INPUT ‖ id_key)</c>.
/// </summary>
public sealed class CryptConfig
{
    // sha256(b"Proxmox Backup Encryption Key Fingerprint"), hard-coded exactly as upstream.
    private static readonly byte[] FingerprintInput =
    {
        110, 208, 239, 119, 71, 31, 255, 77, 85, 199, 168, 254, 74, 157, 182, 33,
        97, 64, 127, 19, 76, 114, 93, 223, 48, 153, 45, 37, 236, 69, 237, 38,
    };

    private const int HeaderSize = 12;       // magic[8] + crc[4]
    private const int IvSize = 16;
    private const int TagSize = 16;

    private readonly byte[] _encKey;         // AES-256 key
    private readonly byte[] _idKey;          // digest key derived from _encKey

    public CryptConfig(byte[] encKey)
    {
        ArgumentNullException.ThrowIfNull(encKey);
        if (encKey.Length != 32) throw new ArgumentException("encryption key must be 32 bytes", nameof(encKey));
        _encKey = (byte[])encKey.Clone();
        _idKey = Rfc2898DeriveBytes.Pbkdf2(_encKey, "_id_key"u8.ToArray(), 10, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>Chunk digest in encrypted mode: <c>SHA256(data ‖ id_key)</c>.</summary>
    public byte[] ComputeDigest(ReadOnlySpan<byte> data)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(data);
        sha.AppendData(_idKey);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Manifest authentication tag: <c>HMAC-SHA256(id_key, data)</c>, matching upstream
    /// <c>CryptConfig::compute_auth_tag</c> (the HMAC key is the same derived <c>id_key</c> used for
    /// chunk digests, wrapped as an HMAC PKey). Used to sign the backup manifest (#6).
    /// </summary>
    public byte[] ComputeAuthTag(ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(_idKey);
        return hmac.ComputeHash(data.ToArray());
    }

    /// <summary>Key fingerprint (32 bytes) — identifies the key in the backup manifest.</summary>
    public byte[] Fingerprint() => ComputeDigest(FingerprintInput);

    /// <summary>Colon-separated lowercase hex fingerprint, as the manifest and keyfile store it.</summary>
    public string FingerprintHex()
    {
        var fp = Fingerprint();
        return string.Join(':', fp.Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Encode <paramref name="plaintext"/> as an encrypted DataBlob:
    /// <c>magic[8] | crc32-le[4] | iv[16] | tag[16] | ciphertext</c>. When <paramref name="compress"/>
    /// is set and zstd shrinks the data, the compressed-encrypted magic is used.
    /// </summary>
    public byte[] EncryptBlob(ReadOnlySpan<byte> plaintext, bool compress = true, int compressionLevel = 1)
    {
        byte[] payload;
        byte[] magic;
        if (compress)
        {
            using var compressor = new Compressor(compressionLevel);
            var compressed = compressor.Wrap(plaintext.ToArray()).ToArray();
            if (compressed.Length < plaintext.Length)
            {
                payload = compressed;
                magic = FileFormats.EncrComprBlobMagic;
            }
            else
            {
                payload = plaintext.ToArray();
                magic = FileFormats.EncryptedBlobMagic;
            }
        }
        else
        {
            payload = plaintext.ToArray();
            magic = FileFormats.EncryptedBlobMagic;
        }

        var iv = new byte[IvSize];
        RandomNumberGenerator.Fill(iv);
        var (ciphertext, tag) = GcmEncrypt(payload, iv);

        var blob = new byte[HeaderSize + IvSize + TagSize + ciphertext.Length];
        magic.CopyTo(blob.AsSpan(0, 8));
        iv.CopyTo(blob.AsSpan(HeaderSize, IvSize));
        tag.CopyTo(blob.AsSpan(HeaderSize + IvSize, TagSize));
        ciphertext.CopyTo(blob.AsSpan(HeaderSize + IvSize + TagSize));

        // CRC covers the bytes after the full encrypted header (iv+tag+ciphertext excluded up to
        // header_size = 44); upstream computes it over raw_data[44..] = the ciphertext.
        uint crc = Crc32.HashToUInt32(blob.AsSpan(HeaderSize + IvSize + TagSize));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8, 4), crc);
        return blob;
    }

    /// <summary>Decode an encrypted DataBlob back to plaintext, verifying the CRC and GCM tag.</summary>
    public byte[] DecryptBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize + IvSize + TagSize)
            throw new InvalidDataException("encrypted DataBlob shorter than its header");

        var magic = blob[..8];
        bool compressed = magic.SequenceEqual(FileFormats.EncrComprBlobMagic);
        if (!compressed && !magic.SequenceEqual(FileFormats.EncryptedBlobMagic))
            throw new InvalidDataException("not an encrypted DataBlob");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8, 4));
        var iv = blob.Slice(HeaderSize, IvSize);
        var tag = blob.Slice(HeaderSize + IvSize, TagSize);
        var ciphertext = blob[(HeaderSize + IvSize + TagSize)..];

        uint actualCrc = Crc32.HashToUInt32(ciphertext);
        if (actualCrc != storedCrc)
            throw new InvalidDataException($"encrypted DataBlob CRC mismatch: stored {storedCrc:x8}, computed {actualCrc:x8}");

        var payload = GcmDecrypt(ciphertext.ToArray(), iv.ToArray(), tag.ToArray());
        if (!compressed) return payload;

        using var decompressor = new Decompressor();
        return decompressor.Unwrap(payload).ToArray();
    }

    private (byte[] ciphertext, byte[] tag) GcmEncrypt(byte[] plaintext, byte[] iv)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(_encKey), TagSize * 8, iv, Array.Empty<byte>()));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len); // appends the tag
        int ctLen = output.Length - TagSize;
        return (output[..ctLen], output[ctLen..]);
    }

    private byte[] GcmDecrypt(byte[] ciphertext, byte[] iv, byte[] tag)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(_encKey), TagSize * 8, iv, Array.Empty<byte>()));
        var input = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(input, 0);
        tag.CopyTo(input, ciphertext.Length);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);
        return output[..len];
    }
}
