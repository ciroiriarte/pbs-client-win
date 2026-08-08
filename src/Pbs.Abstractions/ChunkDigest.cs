using System.Security.Cryptography;

namespace Pbs.Abstractions;

/// <summary>
/// A 32-byte SHA-256 content digest identifying a chunk in a Proxmox Backup Server datastore.
/// PBS addresses every chunk by the SHA-256 of its <em>plaintext</em> contents, so this type is
/// the dedup key used both locally (the "known chunks" set) and on the wire.
/// </summary>
public readonly struct ChunkDigest : IEquatable<ChunkDigest>
{
    /// <summary>Length in bytes of a SHA-256 digest.</summary>
    public const int Size = 32;

    private readonly byte[] _bytes;

    private ChunkDigest(byte[] bytes) => _bytes = bytes;

    /// <summary>The raw 32 digest bytes.</summary>
    public ReadOnlySpan<byte> Span => _bytes is null ? default : _bytes;

    /// <summary>True for a default-constructed (uninitialized) digest.</summary>
    public bool IsEmpty => _bytes is null;

    /// <summary>Compute the digest of <paramref name="data"/> (SHA-256 of the plaintext).</summary>
    public static ChunkDigest Compute(ReadOnlySpan<byte> data)
    {
        var buffer = new byte[Size];
        SHA256.HashData(data, buffer);
        return new ChunkDigest(buffer);
    }

    /// <summary>Wrap an existing 32-byte digest. The array is copied.</summary>
    public static ChunkDigest FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"A chunk digest must be exactly {Size} bytes.", nameof(bytes));
        return new ChunkDigest(bytes.ToArray());
    }

    /// <summary>Parse a 64-character lowercase/uppercase hex digest.</summary>
    public static ChunkDigest Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != Size * 2)
            throw new ArgumentException($"A chunk digest must be {Size * 2} hex characters.", nameof(hex));
        return new ChunkDigest(Convert.FromHexString(hex));
    }

    /// <summary>Lowercase hex representation, as PBS names chunk files.</summary>
    public string ToHex() => _bytes is null ? string.Empty : Convert.ToHexString(_bytes).ToLowerInvariant();

    public bool Equals(ChunkDigest other)
    {
        if (_bytes is null || other._bytes is null)
            return _bytes is null && other._bytes is null;
        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    public override bool Equals(object? obj) => obj is ChunkDigest other && Equals(other);

    public override int GetHashCode()
    {
        if (_bytes is null) return 0;
        // The digest is already uniformly distributed; the first 4 bytes make a fine hash.
        return BitConverter.ToInt32(_bytes, 0);
    }

    public override string ToString() => ToHex();

    public static bool operator ==(ChunkDigest left, ChunkDigest right) => left.Equals(right);
    public static bool operator !=(ChunkDigest left, ChunkDigest right) => !left.Equals(right);
}
