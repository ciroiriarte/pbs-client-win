using Pbs.Cli;
using Pbs.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Backs up a synthetic block image via the fixed-index (FIDX) path and restores it byte-for-byte.
/// Exercises fixed_index/fixed_chunk/fixed_close, block dedup, and image restore against live PBS.
/// </summary>
public class FixedImageBackupTests
{
    private readonly ITestOutputHelper _out;
    public FixedImageBackupTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task ImageBackup_Restore_BytesMatch_WithBlockDedup()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        const int chunkSize = 4 * 1024 * 1024; // PBS fixed images use a 4 MiB chunk size
        // 3 blocks: block1 duplicates block0 (dedup), then a short 2 MiB tail.
        var block0 = new byte[chunkSize];
        new Random(3).NextBytes(block0);
        var tail = new byte[chunkSize / 2];
        new Random(4).NextBytes(tail);
        var image = block0.Concat(block0).Concat(tail).ToArray();

        const string archive = "disk0.img.fidx";
        var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var backupId = $"img-it-{backupTime}";

        DynamicArchive.UploadStats stats;
        await using (var s = await BackupSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime))
        {
            stats = await FixedImage.UploadAsync(s, archive, new MemoryStream(image), image.Length);
            await s.FinishAsync();
        }
        _out.WriteLine($"image={image.Length} {stats}");
        Assert.Equal(3, stats.TotalChunks);
        Assert.Equal(2, stats.UploadedChunks); // block0 == block1 -> one upload saved

        await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime);
        using var restored = new MemoryStream();
        var written = await reader.RestoreFixedArchiveAsync(archive, restored);
        Assert.Equal(image.Length, written);
        Assert.True(image.AsSpan().SequenceEqual(restored.ToArray()), "restored image differs from source");
    }
}
