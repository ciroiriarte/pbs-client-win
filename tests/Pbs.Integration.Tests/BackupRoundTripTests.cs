using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Drives a full backup against the live PBS server: chunk → DataBlob → upload over the upgraded
/// HTTP/2 session → dynamic index → close → manifest → finish. A successful finish means the
/// server accepted our index_csum and manifest; we then confirm the snapshot is listed.
/// </summary>
public class BackupRoundTripTests
{
    private readonly ITestOutputHelper _out;
    public BackupRoundTripTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task BackupDynamicArchive_ThenSnapshotIsListed()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        // ~4 MiB with a duplicated region so the dedup path is exercised too.
        var rng = new Random(20260808);
        var half = new byte[2 * 1024 * 1024];
        rng.NextBytes(half);
        var data = half.Concat(half).ToArray(); // second half duplicates the first

        var options = new ChunkerOptions { MinSize = 256 * 1024, MaxSize = 1024 * 1024, MaskBits = 18 };
        var chunks = new ContentDefinedChunker(options).Split(data);
        _out.WriteLine($"data={data.Length} bytes, chunks={chunks.Count}");

        const string archiveName = "test.pxar.didx";
        var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var backupId = $"winclient-test-{backupTime}"; // unique per run to avoid group-lock collisions

        await using (var session = await BackupSession.OpenAsync(
                         PbsTestServer.Options(), "host", backupId, backupTime))
        {
            var wid = await session.CreateDynamicIndexAsync(archiveName);

            var seen = new HashSet<ChunkDigest>();
            int uploaded = 0;
            foreach (var c in chunks)
            {
                if (!seen.Add(c.Digest)) continue; // dedup: upload each unique chunk once
                var blob = DataBlob.Encode(data.AsSpan((int)c.Offset, c.Length).ToArray(), compress: true);
                await session.UploadDynamicChunkAsync(wid, c, blob);
                uploaded++;
            }
            _out.WriteLine($"unique chunks uploaded={uploaded}");

            await session.AppendDynamicIndexAsync(wid, chunks);
            var csum = DynamicIndex.ComputeCsum(chunks);
            await session.CloseDynamicIndexAsync(archiveName, wid, chunks.Count, data.Length, csum);
            await session.FinishAsync();
        }

        // Confirm the snapshot now exists server-side.
        using var client = PbsTestServer.CreateClient();
        var snapshots = await client.ListSnapshotsAsync(backupId);
        _out.WriteLine($"snapshots for {backupId}: {snapshots.Count}");
        Assert.NotEmpty(snapshots);

        // Restore the archive over the reader protocol and byte-compare against the original.
        await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime);
        using var restored = new MemoryStream();
        var restoredLen = await reader.RestoreDynamicArchiveAsync(archiveName, restored);
        _out.WriteLine($"restored {restoredLen} bytes");

        Assert.Equal(data.Length, restoredLen);
        Assert.True(data.AsSpan().SequenceEqual(restored.ToArray()), "restored bytes differ from original");
    }
}
