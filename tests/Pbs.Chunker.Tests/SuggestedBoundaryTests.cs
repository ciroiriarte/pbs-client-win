using Pbs.Chunker;
using Xunit;

namespace Pbs.Chunker.Tests;

/// <summary>Suggested boundaries for split-archive payload streams (issue #33), like upstream's PayloadChunker.</summary>
public class SuggestedBoundaryTests
{
    private static readonly ChunkerOptions Small = new() { MinSize = 1024, MaxSize = 16 * 1024, MaskBits = 12 };

    private static async Task<List<int>> ChunkLengths(byte[] data, IReadOnlyList<long>? suggested, long basePosition = 0)
    {
        var lengths = new List<int>();
        await foreach (var c in new ContentDefinedChunker(Small).SplitStreamAsync(new MemoryStream(data), suggested, basePosition))
            lengths.Add(c.Length);
        return lengths;
    }

    private static byte[] Random(int n)
    {
        var data = new byte[n];
        new Random(42).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task NoSuggestions_MatchesPlainChunking()
    {
        var data = Random(200_000);
        var plain = new List<int>();
        await foreach (var c in new ContentDefinedChunker(Small).SplitStreamAsync(new MemoryStream(data)))
            plain.Add(c.Length);
        Assert.Equal(plain, await ChunkLengths(data, null));
        Assert.Equal(plain, await ChunkLengths(data, Array.Empty<long>()));
    }

    [Fact]
    public async Task CutsAtSuggestion_BeforeContentDefinedBoundary()
    {
        var data = Random(200_000);
        var plainFirst = (await ChunkLengths(data, null))[0];
        Assert.True(plainFirst > Small.MinSize + 10, "test needs a first chunk longer than the minimum");

        var lengths = await ChunkLengths(data, new long[] { Small.MinSize + 5 });
        Assert.Equal(Small.MinSize + 5, lengths[0]);
        Assert.Equal(data.Length, lengths.Sum());
    }

    [Fact]
    public async Task SuggestionBelowMinSize_IsIgnored()
    {
        var data = Random(200_000);
        var plain = await ChunkLengths(data, null);
        Assert.Equal(plain, await ChunkLengths(data, new long[] { 10, 500 }));
    }

    [Fact]
    public async Task SuggestionsAreAbsolute_RelativeToBasePosition()
    {
        var data = Random(200_000);
        // stream starts at absolute 1_000_000; suggest absolute 1_000_000 + 3000
        var lengths = await ChunkLengths(data, new long[] { 999_000, 1_003_000 }, basePosition: 1_000_000);
        var plainFirst = (await ChunkLengths(data, null))[0];
        Assert.Equal(Math.Min(3000, plainFirst), lengths[0]);
        Assert.Equal(data.Length, lengths.Sum());
    }

    [Fact]
    public async Task Suggestions_StartChunks_UnlessTooCloseToThePreviousCut()
    {
        var data = Random(100_000);
        var suggested = Enumerable.Range(1, 40).Select(i => (long)i * 2400).ToList(); // every 2400 bytes > MinSize
        var lengths = await ChunkLengths(data, suggested);
        long pos = 0;
        var ends = lengths.Select(l => pos += l).ToHashSet();
        // A suggestion is honoured unless an earlier cut lies less than MinSize before it (that chunk
        // would be too small), exactly upstream's rule.
        Assert.All(suggested.Where(s => s < data.Length), s =>
            Assert.True(ends.Contains(s) || ends.Any(e => e < s && s - e < Small.MinSize), $"suggestion {s} skipped"));
        Assert.True(suggested.Count(ends.Contains) > suggested.Count / 2);
    }
}
