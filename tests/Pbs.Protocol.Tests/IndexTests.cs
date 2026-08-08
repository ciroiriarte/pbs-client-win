using Pbs.Abstractions;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class FixedIndexTests
{
    private static byte[] Uuid16(int seed)
    {
        var u = new byte[16];
        new Random(seed).NextBytes(u);
        return u;
    }

    [Fact]
    public void BuildParse_RoundTrips()
    {
        long chunkSize = 4 * 1024 * 1024;
        long size = chunkSize * 3 + 123;         // 4 chunks, last one short
        var digests = new List<ChunkDigest>
        {
            ChunkDigest.Compute("a"u8), ChunkDigest.Compute("b"u8),
            ChunkDigest.Compute("c"u8), ChunkDigest.Compute("d"u8),
        };

        var bytes = FixedIndex.Build(size, chunkSize, digests, ctimeSeconds: 1_700_000_000, Uuid16(1));

        Assert.Equal(FileFormats.IndexHeaderSize + digests.Count * 32, bytes.Length);
        Assert.Equal(FileFormats.FixedSizedChunkIndexMagic, bytes.AsSpan(0, 8).ToArray());

        var (parsedSize, parsedChunk, parsedDigests) = FixedIndex.Parse(bytes);
        Assert.Equal(size, parsedSize);
        Assert.Equal(chunkSize, parsedChunk);
        Assert.Equal(digests, parsedDigests);
    }

    [Fact]
    public void WrongDigestCount_Throws()
    {
        var digests = new List<ChunkDigest> { ChunkDigest.Compute("x"u8) };
        Assert.Throws<ArgumentException>(() =>
            FixedIndex.Build(size: 10_000_000, chunkSize: 4_000_000, digests, 0, Uuid16(2)));
    }

    [Fact]
    public void CorruptedDigest_FailsCsum()
    {
        var digests = new List<ChunkDigest> { ChunkDigest.Compute("a"u8), ChunkDigest.Compute("b"u8) };
        var bytes = FixedIndex.Build(size: 8, chunkSize: 4, digests, 0, Uuid16(3));
        bytes[FileFormats.IndexHeaderSize] ^= 0xFF; // corrupt first digest, leave stored csum

        Assert.Throws<InvalidDataException>(() => FixedIndex.Parse(bytes));
    }
}

public class DynamicIndexTests
{
    private static byte[] Uuid16(int seed)
    {
        var u = new byte[16];
        new Random(seed).NextBytes(u);
        return u;
    }

    [Fact]
    public void BuildParse_RoundTrips()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, 100, ChunkDigest.Compute("one"u8)),
            new(100, 250, ChunkDigest.Compute("two"u8)),
            new(350, 50, ChunkDigest.Compute("three"u8)),
        };

        var bytes = DynamicIndex.Build(chunks, ctimeSeconds: 1_700_000_000, Uuid16(1));

        Assert.Equal(FileFormats.IndexHeaderSize + chunks.Count * 40, bytes.Length);
        Assert.Equal(FileFormats.DynamicSizedChunkIndexMagic, bytes.AsSpan(0, 8).ToArray());

        var entries = DynamicIndex.Parse(bytes);
        Assert.Equal(chunks.Count, entries.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i].End, entries[i].EndOffset);
            Assert.Equal(chunks[i].Digest, entries[i].Digest);
        }
    }

    [Fact]
    public void CorruptedEntry_FailsCsum()
    {
        var chunks = new List<ChunkRef> { new(0, 10, ChunkDigest.Compute("z"u8)) };
        var bytes = DynamicIndex.Build(chunks, 0, Uuid16(2));
        bytes[FileFormats.IndexHeaderSize] ^= 0xFF; // corrupt the end-offset of entry 0

        Assert.Throws<InvalidDataException>(() => DynamicIndex.Parse(bytes));
    }
}
