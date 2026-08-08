using Pbs.Abstractions;

namespace Pbs.Chunker;

/// <summary>
/// Fixed-size chunker for block/image (<c>.fidx</c>) archives. Every chunk is exactly
/// <see cref="ChunkSize"/> bytes except a possible short final chunk.
/// </summary>
public sealed class FixedSizeChunker
{
    public int ChunkSize { get; }

    public FixedSizeChunker(int chunkSize = ChunkerOptions.FixedChunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        ChunkSize = chunkSize;
    }

    /// <summary>Split <paramref name="data"/> into fixed-size chunks with SHA-256 digests.</summary>
    public IReadOnlyList<ChunkRef> Split(ReadOnlySpan<byte> data)
    {
        var result = new List<ChunkRef>();
        int pos = 0;
        int n = data.Length;
        while (pos < n)
        {
            int len = Math.Min(ChunkSize, n - pos);
            var slice = data.Slice(pos, len);
            result.Add(new ChunkRef(pos, len, ChunkDigest.Compute(slice)));
            pos += len;
        }
        return result;
    }
}
