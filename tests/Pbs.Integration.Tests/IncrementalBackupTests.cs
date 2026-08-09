using Pbs.Chunker;
using Pbs.Cli;
using Pbs.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Verifies incremental backups: a second backup of unchanged data reuses the previous snapshot's
/// chunks without re-uploading; a partial change re-uploads only the affected chunks; and the
/// incremental snapshot still restores byte-for-byte.
/// </summary>
public class IncrementalBackupTests
{
    private readonly ITestOutputHelper _out;
    public IncrementalBackupTests(ITestOutputHelper output) => _out = output;

    // Small chunks so a modest buffer yields many chunks and partial reuse is observable.
    private static readonly ChunkerOptions Opts = new() { MinSize = 64 * 1024, MaxSize = 256 * 1024, MaskBits = 17 };
    private const string Archive = "data.img.didx";

    [SkippableFact]
    public async Task SecondBackup_ReusesChunks_AndPartialChangeReuploadsOnlyChanged()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        var data = new byte[4 * 1024 * 1024];
        new Random(31).NextBytes(data);

        var id = $"inc-it-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var first = await BackupOnce(id, t0, data);
        _out.WriteLine($"first:  {first}");
        Assert.Equal(0, first.ReusedChunks);
        Assert.True(first.UploadedChunks > 1, "expected multiple chunks");

        var second = await BackupOnce(id, t0 + 1, data); // identical
        _out.WriteLine($"second: {second}");
        Assert.Equal(0, second.UploadedChunks);
        Assert.Equal(second.TotalChunks, second.ReusedChunks); // everything reused

        // Change one region in the middle; most chunks should still be reused.
        new Random(99).NextBytes(data.AsSpan(2 * 1024 * 1024, 128 * 1024));
        var third = await BackupOnce(id, t0 + 2, data);
        _out.WriteLine($"third:  {third}");
        Assert.True(third.UploadedChunks > 0, "changed region should upload something");
        Assert.True(third.ReusedChunks > 0, "unchanged regions should be reused");

        // The incremental snapshot restores byte-for-byte.
        await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", id, t0 + 2);
        using var restored = new MemoryStream();
        await reader.RestoreDynamicArchiveAsync(Archive, restored);
        Assert.True(data.AsSpan().SequenceEqual(restored.ToArray()), "restored incremental differs from source");
    }

    private static async Task<DynamicArchive.UploadStats> BackupOnce(string id, long time, byte[] data)
    {
        await using var s = await BackupSession.OpenAsync(PbsTestServer.Options(), "host", id, time);
        var stats = await DynamicArchive.UploadAsync(s, Archive, data, options: Opts, incremental: true);
        await s.FinishAsync();
        return stats;
    }
}
