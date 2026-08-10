using Pbs.Abstractions;

namespace Pbs.Protocol;

public readonly record struct ChunkSpan(long Start, long End, ChunkDigest Digest);

public static class ChunkRangeReader
{
    public static async ValueTask<byte[]> ReadAsync(
        IReadOnlyList<ChunkSpan> spans,
        long offset,
        long length,
        Func<ChunkDigest, CancellationToken, ValueTask<byte[]>> chunkProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(chunkProvider);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be non-negative");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "length must be non-negative");
        if (length == 0 || spans.Count == 0)
            return Array.Empty<byte>();

        var totalSize = spans[^1].End;
        if (offset >= totalSize)
            return Array.Empty<byte>();

        var readLength = Math.Min(length, totalSize - offset);
        var result = new byte[checked((int)readLength)];
        var readEnd = offset + readLength;
        var spanIndex = FindFirstCoveringSpan(spans, offset);
        var dest = 0;

        while (spanIndex < spans.Count && offset < readEnd)
        {
            ct.ThrowIfCancellationRequested();

            var span = spans[spanIndex];
            var chunk = await chunkProvider(span.Digest, ct);
            var copyStart = Math.Max(offset, span.Start);
            var copyEnd = Math.Min(readEnd, span.End);
            var chunkOffset = checked((int)(copyStart - span.Start));
            var count = checked((int)(copyEnd - copyStart));

            if (chunkOffset + count > chunk.Length)
                throw new InvalidDataException($"chunk {span.Digest} shorter than index span");

            chunk.AsSpan(chunkOffset, count).CopyTo(result.AsSpan(dest, count));
            dest += count;
            offset = copyEnd;
            spanIndex++;
        }

        return result;
    }

    private static int FindFirstCoveringSpan(IReadOnlyList<ChunkSpan> spans, long offset)
    {
        var lo = 0;
        var hi = spans.Count - 1;
        var result = spans.Count;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (spans[mid].End > offset)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return result;
    }
}
