using System.Security.Cryptography;
using Pbs.Abstractions;
using Pbs.Chunker;
using Xunit;

namespace Pbs.Chunker.Tests;

public class ContentDefinedChunkerTests
{
    // Small bounds so tests exercise many boundaries on modest inputs.
    private static ChunkerOptions SmallOptions => new() { MinSize = 64, MaxSize = 4096, MaskBits = 8 };

    private static byte[] DeterministicData(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void Split_IsDeterministic()
    {
        var data = DeterministicData(200_000, seed: 1);
        var a = new ContentDefinedChunker(SmallOptions).Split(data);
        var b = new ContentDefinedChunker(SmallOptions).Split(data);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Offset, b[i].Offset);
            Assert.Equal(a[i].Length, b[i].Length);
            Assert.Equal(a[i].Digest, b[i].Digest);
        }
    }

    // #4: the streaming chunker must yield byte-identical chunks (and digests) to the in-memory Split,
    // otherwise a streamed backup would produce a different .didx and break dedup/restore.
    [Theory]
    [InlineData(0)]        // empty
    [InlineData(1)]        // single byte
    [InlineData(63)]       // < MinSize
    [InlineData(4096)]     // == MaxSize
    [InlineData(4097)]     // just over MaxSize (forces a max-size cut)
    [InlineData(200_000)]  // many content-defined boundaries
    [InlineData(524_288)]
    public async Task SplitStreamAsync_MatchesSplit(int length)
    {
        var data = DeterministicData(length, seed: 7);
        var chunker = new ContentDefinedChunker(SmallOptions);
        var expected = chunker.Split(data);

        var streamed = new List<byte[]>();
        using var ms = new MemoryStream(data, writable: false);
        await foreach (var chunk in chunker.SplitStreamAsync(ms))
            streamed.Add(chunk);

        Assert.Equal(expected.Count, streamed.Count);
        long off = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Length, streamed[i].Length);
            Assert.Equal(data.AsSpan((int)off, expected[i].Length).ToArray(), streamed[i]);
            Assert.Equal(expected[i].Digest, ChunkDigest.Compute(streamed[i]));
            off += streamed[i].Length;
        }
        Assert.Equal(length, off);
        Assert.Equal(data, streamed.SelectMany(c => c).ToArray()); // reassembles to the input
    }

    [Fact]
    public void Chunks_ReassembleToInput()
    {
        var data = DeterministicData(500_000, seed: 2);
        var chunks = new ContentDefinedChunker(SmallOptions).Split(data);

        // Offsets are contiguous and cover the whole input.
        long expectedOffset = 0;
        foreach (var c in chunks)
        {
            Assert.Equal(expectedOffset, c.Offset);
            expectedOffset = c.End;
        }
        Assert.Equal(data.Length, expectedOffset);
    }

    [Fact]
    public void Chunks_RespectMinAndMaxSize()
    {
        var opts = SmallOptions;
        var data = DeterministicData(500_000, seed: 3);
        var chunks = new ContentDefinedChunker(opts).Split(data);

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Length <= opts.MaxSize, $"chunk {i} exceeds MaxSize");
            // Every chunk except possibly the last must be at least MinSize.
            if (i < chunks.Count - 1)
                Assert.True(chunks[i].Length >= opts.MinSize, $"chunk {i} below MinSize");
        }
    }

    [Fact]
    public void Digest_MatchesSha256OfChunkBytes()
    {
        var data = DeterministicData(300_000, seed: 4);
        var chunks = new ContentDefinedChunker(SmallOptions).Split(data);

        foreach (var c in chunks)
        {
            var slice = data.AsSpan((int)c.Offset, c.Length);
            var expected = ChunkDigest.FromBytes(SHA256.HashData(slice.ToArray()));
            Assert.Equal(expected, c.Digest);
        }
    }

    [Fact]
    public void Boundaries_AreStableWhenSuffixAppended()
    {
        // The core dedup property: extending a source at the end must not change the chunking of
        // the prefix (every chunk of `base` except its last reappears unchanged in `extended`).
        var baseData = DeterministicData(400_000, seed: 5);
        var extra = DeterministicData(50_000, seed: 6);
        var extended = baseData.Concat(extra).ToArray();

        var chunker = new ContentDefinedChunker(SmallOptions);
        var baseChunks = chunker.Split(baseData);
        var extChunks = chunker.Split(extended);

        // All but the final base chunk must match the extended chunking one-for-one.
        for (int i = 0; i < baseChunks.Count - 1; i++)
        {
            Assert.Equal(baseChunks[i].Offset, extChunks[i].Offset);
            Assert.Equal(baseChunks[i].Length, extChunks[i].Length);
            Assert.Equal(baseChunks[i].Digest, extChunks[i].Digest);
        }
    }

    [Fact]
    public void EmptyInput_ProducesNoChunks()
    {
        var chunks = new ContentDefinedChunker(SmallOptions).Split(ReadOnlySpan<byte>.Empty);
        Assert.Empty(chunks);
    }

    [Fact]
    public void IdenticalRegions_ProduceIdenticalDigests()
    {
        // Two copies of the same block back-to-back should yield at least one repeated digest,
        // demonstrating deduplication potential.
        var block = DeterministicData(100_000, seed: 7);
        var doubled = block.Concat(block).ToArray();

        var chunks = new ContentDefinedChunker(SmallOptions).Split(doubled);
        var distinct = chunks.Select(c => c.Digest).Distinct().Count();
        Assert.True(distinct < chunks.Count, "expected some duplicate chunks across identical regions");
    }
}

public class FixedSizeChunkerTests
{
    [Fact]
    public void Split_ProducesFixedSizedChunksWithShortTail()
    {
        var data = new byte[10_000];
        new Random(42).NextBytes(data);
        var chunks = new FixedSizeChunker(chunkSize: 4096).Split(data);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(4096, chunks[0].Length);
        Assert.Equal(4096, chunks[1].Length);
        Assert.Equal(10_000 - 8192, chunks[2].Length);

        long total = chunks.Sum(c => (long)c.Length);
        Assert.Equal(data.Length, total);
    }

    [Fact]
    public void Digest_MatchesSha256()
    {
        var data = new byte[9000];
        new Random(43).NextBytes(data);
        var chunks = new FixedSizeChunker(chunkSize: 4096).Split(data);

        foreach (var c in chunks)
        {
            var expected = ChunkDigest.FromBytes(SHA256.HashData(data.AsSpan((int)c.Offset, c.Length).ToArray()));
            Assert.Equal(expected, c.Digest);
        }
    }
}

public class ChunkDigestTests
{
    [Fact]
    public void HexRoundTrips()
    {
        var digest = ChunkDigest.Compute("hello world"u8);
        var parsed = ChunkDigest.Parse(digest.ToHex());
        Assert.Equal(digest, parsed);
    }

    [Fact]
    public void KnownSha256Vector()
    {
        // SHA-256("abc") — a standard NIST vector.
        var digest = ChunkDigest.Compute("abc"u8);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", digest.ToHex());
    }

    [Fact]
    public void EqualityAndHashCode()
    {
        var a = ChunkDigest.Compute("same"u8);
        var b = ChunkDigest.Compute("same"u8);
        var c = ChunkDigest.Compute("different"u8);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);

        var set = new HashSet<ChunkDigest> { a };
        Assert.Contains(b, set);       // dedup key behavior
        Assert.DoesNotContain(c, set);
    }
}
