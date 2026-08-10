using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Live check for issue #12: on-demand ranged reads (<see cref="ReaderSession.ReadRangeAsync"/> and
/// <see cref="ReaderSession.OpenArchiveStream"/>) return exactly the same bytes as the corresponding
/// slice of a full restore, without downloading the whole archive up front.
/// </summary>
public class RangedReadTests
{
    private readonly ITestOutputHelper _out;
    public RangedReadTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task ReadRange_And_ArchiveStream_MatchFullRestoreSlices()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        var rng = new Random(20260810);
        var half = new byte[2 * 1024 * 1024];
        rng.NextBytes(half);
        var data = half.Concat(half).ToArray(); // ~4 MiB, second half duplicates the first

        var options = new ChunkerOptions { MinSize = 256 * 1024, MaxSize = 1024 * 1024, MaskBits = 18 };
        var chunks = new ContentDefinedChunker(options).Split(data);

        const string archiveName = "ranged.pxar.didx";
        var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var backupId = $"ranged-read-test-{backupTime}";

        await using (var session = await BackupSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime))
        {
            var wid = await session.CreateDynamicIndexAsync(archiveName);
            var seen = new HashSet<ChunkDigest>();
            foreach (var c in chunks)
            {
                if (!seen.Add(c.Digest)) continue;
                var blob = DataBlob.Encode(data.AsSpan((int)c.Offset, c.Length).ToArray(), compress: true);
                await session.UploadDynamicChunkAsync(wid, c, blob);
            }
            await session.AppendDynamicIndexAsync(wid, chunks);
            await session.CloseDynamicIndexAsync(archiveName, wid, chunks.Count, data.Length, DynamicIndex.ComputeCsum(chunks));
            await session.FinishAsync();
        }

        await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime);

        // Reference = full restore (must equal the original), then compare ranged reads against it.
        using var restored = new MemoryStream();
        await reader.RestoreDynamicArchiveAsync(archiveName, restored);
        var reference = restored.ToArray();
        Assert.True(data.AsSpan().SequenceEqual(reference), "full restore differs from original");

        Assert.Equal(reference.Length, await reader.GetArchiveSizeAsync(archiveName));

        var ranges = new (long Offset, long Length)[]
        {
            (0, 100),
            (0, reference.Length),
            (reference.Length - 50, 200),   // clamped past EOF
            (1_500_000, 1_500_000),         // spans several chunks
            (reference.Length / 2 - 7, 33), // odd boundary
            (reference.Length, 10),         // at EOF -> empty
        };
        foreach (var (offset, length) in ranges)
        {
            var slice = await reader.ReadRangeAsync(archiveName, offset, length);
            var expected = Slice(reference, offset, length);
            Assert.True(expected.AsSpan().SequenceEqual(slice), $"ReadRange({offset},{length}) mismatch");
        }
        _out.WriteLine($"{ranges.Length} ranged reads matched the full restore");

        // Seekable stream view: random-access reads reassemble to the reference.
        var size = await reader.GetArchiveSizeAsync(archiveName);
        await using var stream = reader.OpenArchiveStream(archiveName, size);
        Assert.Equal(reference.Length, stream.Length);
        for (int i = 0; i < 20; i++)
        {
            long offset = rng.NextInt64(0, size);
            int len = (int)Math.Min(rng.NextInt64(1, 300_000), size - offset);
            stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = await stream.ReadAsync(buf.AsMemory(read, len - read));
                if (n == 0) break;
                read += n;
            }
            Assert.Equal(len, read);
            Assert.True(reference.AsSpan((int)offset, len).SequenceEqual(buf), $"stream read at {offset}+{len} mismatch");
        }
        _out.WriteLine("archive stream random-access reads matched");
    }

    private static byte[] Slice(byte[] data, long offset, long length)
    {
        if (length == 0 || offset >= data.Length) return Array.Empty<byte>();
        int count = (int)Math.Min(length, data.Length - offset);
        return data.AsSpan((int)offset, count).ToArray();
    }
}
