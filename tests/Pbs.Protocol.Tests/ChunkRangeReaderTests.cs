using Pbs.Abstractions;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class ChunkRangeReaderTests
{
    [Fact]
    public async Task DynamicChunks_ReassembleRandomRanges()
    {
        var chunks = BuildChunks(17, new[] { 1, 19, 1024, 7, 4099, 300, 8192 });
        var spans = BuildDynamicSpans(chunks);

        await AssertRangesMatch(spans, chunks);
    }

    [Fact]
    public async Task FixedChunks_ReassembleRandomRanges()
    {
        // Small chunkSize keeps the test fast: the fixed-span math (i*chunkSize with a short final
        // block) is size-agnostic, and large arrays make xUnit's element-wise Assert.Equal crawl.
        const int chunkSize = 4096;
        var chunks = BuildChunks(23, new[] { chunkSize, chunkSize, 123 });
        var spans = BuildFixedSpans(chunks, chunkSize);

        await AssertRangesMatch(spans, chunks);
    }

    [Fact]
    public async Task ReadAsync_FetchesOnlyCoveringChunks()
    {
        var chunks = BuildChunks(31, new[] { 10, 10, 10, 10 });
        var spans = BuildDynamicSpans(chunks);
        var byDigest = chunks.ToDictionary(x => ChunkDigest.Compute(x), x => x);
        var fetched = new List<ChunkDigest>();

        var actual = await ChunkRangeReader.ReadAsync(
            spans,
            offset: 12,
            length: 16,
            (digest, _) =>
            {
                fetched.Add(digest);
                return ValueTask.FromResult(byDigest[digest]);
            });

        Assert.Equal(SliceClamped(chunks.SelectMany(x => x).ToArray(), 12, 16), actual);
        Assert.Equal(new[] { spans[1].Digest, spans[2].Digest }, fetched);
    }

    private static async Task AssertRangesMatch(IReadOnlyList<ChunkSpan> spans, IReadOnlyList<byte[]> chunks)
    {
        var all = chunks.SelectMany(x => x).ToArray();
        var byDigest = chunks.ToDictionary(x => ChunkDigest.Compute(x), x => x);
        var random = new Random(1234);
        var ranges = new List<(long Offset, long Length)>
        {
            (0, 0),
            (0, all.Length),
            (0, all.Length + 100),
            (all.Length, 10),
            (all.Length + 25, 10),
            (1, 1),
            (18, 10),
            (Math.Max(0, all.Length - 8), 100),
        };

        foreach (var span in spans)
        {
            ranges.Add((span.Start, Math.Min(32, span.End - span.Start)));
            ranges.Add((Math.Max(0, span.End - 5), 10));
        }

        for (int i = 0; i < 80; i++)
        {
            var offset = random.NextInt64(0, all.Length + 1000);
            var length = random.NextInt64(0, all.Length + 1000);
            ranges.Add((offset, length));
        }

        foreach (var (offset, length) in ranges)
        {
            var actual = await ChunkRangeReader.ReadAsync(
                spans,
                offset,
                length,
                (digest, _) => ValueTask.FromResult(byDigest[digest]));
            var expected = SliceClamped(all, offset, length);

            Assert.Equal(expected, actual);
        }
    }

    private static IReadOnlyList<byte[]> BuildChunks(int seed, IReadOnlyList<int> sizes)
    {
        var random = new Random(seed);
        var chunks = new List<byte[]>(sizes.Count);
        foreach (var size in sizes)
        {
            var chunk = new byte[size];
            random.NextBytes(chunk);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static IReadOnlyList<ChunkSpan> BuildDynamicSpans(IReadOnlyList<byte[]> chunks)
    {
        var spans = new List<ChunkSpan>(chunks.Count);
        long start = 0;
        foreach (var chunk in chunks)
        {
            var end = start + chunk.Length;
            spans.Add(new ChunkSpan(start, end, ChunkDigest.Compute(chunk)));
            start = end;
        }
        return spans;
    }

    private static IReadOnlyList<ChunkSpan> BuildFixedSpans(IReadOnlyList<byte[]> chunks, long chunkSize)
    {
        var size = chunks.Sum(x => x.LongLength);
        var spans = new List<ChunkSpan>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            var start = i * chunkSize;
            var end = Math.Min(start + chunkSize, size);
            spans.Add(new ChunkSpan(start, end, ChunkDigest.Compute(chunks[i])));
        }
        return spans;
    }

    private static byte[] SliceClamped(byte[] data, long offset, long length)
    {
        if (length == 0 || offset >= data.Length)
            return Array.Empty<byte>();

        var count = checked((int)Math.Min(length, data.Length - offset));
        var result = new byte[count];
        data.AsSpan(checked((int)offset), count).CopyTo(result);
        return result;
    }
}
