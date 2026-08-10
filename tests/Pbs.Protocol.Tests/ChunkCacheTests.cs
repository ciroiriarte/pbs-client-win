using Pbs.Abstractions;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class ChunkCacheTests
{
    [Fact]
    public void TryGet_ReturnsHitsAndMisses()
    {
        var cache = new ChunkCache(maxBytes: 10);
        var digest = Digest(1);
        var value = new byte[] { 1, 2, 3 };

        Assert.False(cache.TryGet(digest, out _));

        cache.Add(digest, value);

        Assert.True(cache.TryGet(digest, out var cached));
        Assert.Same(value, cached);
    }

    [Fact]
    public void AddPastBudget_EvictsLeastRecentlyUsed()
    {
        var cache = new ChunkCache(maxBytes: 6);
        var a = Digest(1);
        var b = Digest(2);
        var c = Digest(3);

        cache.Add(a, new byte[] { 1, 1, 1 });
        cache.Add(b, new byte[] { 2, 2, 2 });
        Assert.True(cache.TryGet(a, out _)); // b is now least-recently-used

        cache.Add(c, new byte[] { 3, 3, 3 });

        Assert.True(cache.TryGet(a, out _));
        Assert.False(cache.TryGet(b, out _));
        Assert.True(cache.TryGet(c, out _));
    }

    [Fact]
    public void Add_DeduplicatesExistingDigest()
    {
        var cache = new ChunkCache(maxBytes: 5);
        var a = Digest(1);
        var b = Digest(2);
        var first = new byte[] { 1, 1, 1 };

        cache.Add(a, first);
        cache.Add(a, new byte[] { 9, 9, 9 });
        cache.Add(b, new byte[] { 2, 2 });

        Assert.True(cache.TryGet(a, out var cached));
        Assert.Same(first, cached);
        Assert.True(cache.TryGet(b, out _));
    }

    private static ChunkDigest Digest(byte value) => ChunkDigest.Compute(new[] { value });
}
