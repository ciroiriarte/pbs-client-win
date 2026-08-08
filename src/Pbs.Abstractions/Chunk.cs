namespace Pbs.Abstractions;

/// <summary>
/// A single chunk produced by the chunker: its position in the source stream, its plaintext
/// length, and its content digest. The plaintext bytes are carried separately by the upload
/// pipeline so this record stays cheap to pass around and log.
/// </summary>
/// <param name="Offset">Byte offset of the chunk within the logical source stream.</param>
/// <param name="Length">Plaintext length of the chunk in bytes.</param>
/// <param name="Digest">SHA-256 digest of the plaintext chunk (the dedup key).</param>
public readonly record struct ChunkRef(long Offset, int Length, ChunkDigest Digest)
{
    /// <summary>Exclusive end offset of the chunk within the source stream.</summary>
    public long End => Offset + Length;
}

/// <summary>The kind of PBS index an archive is stored under.</summary>
public enum IndexType
{
    /// <summary>Dynamic (content-defined) chunking — used for file/stream archives (<c>.didx</c>).</summary>
    Dynamic,

    /// <summary>Fixed-size chunking — used for block/image archives (<c>.fidx</c>).</summary>
    Fixed,
}
