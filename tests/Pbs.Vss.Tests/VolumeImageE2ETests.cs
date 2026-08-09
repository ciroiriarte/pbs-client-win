using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Pbs.Cli;
using Pbs.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Vss.Tests;

/// <summary>
/// Full Windows image path: VSS-snapshot a volume, read it raw from the shadow copy, back it up as
/// a fixed (FIDX) image to live PBS, restore, and confirm the image round-trips byte-for-byte. The
/// volume to use comes from the IMG_VOLUME env var (a small throwaway VHD set up by the harness).
/// </summary>
[SupportedOSPlatform("windows")]
public class VolumeImageE2ETests
{
    private readonly ITestOutputHelper _out;
    public VolumeImageE2ETests(ITestOutputHelper output) => _out = output;

    private static bool PbsAvailable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PBS_ENDPOINT")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PBS_TOKEN_SECRET"));

    private static PbsConnectionOptions Options() => new()
    {
        BaseUrl = new Uri(Environment.GetEnvironmentVariable("PBS_ENDPOINT")!),
        Token = new PbsApiToken(Environment.GetEnvironmentVariable("PBS_TOKEN_ID")!, Environment.GetEnvironmentVariable("PBS_TOKEN_SECRET")!),
        Fingerprint = Environment.GetEnvironmentVariable("PBS_FINGERPRINT") is { Length: > 0 } fp ? CertificateFingerprint.Parse(fp) : null,
        Datastore = Environment.GetEnvironmentVariable("PBS_DATASTORE") ?? "backup01",
        Namespace = Environment.GetEnvironmentVariable("PBS_NAMESPACE"),
    };

    [SkippableFact]
    public async Task Volume_VssImageBackup_Restore_HashMatches()
    {
        Skip.IfNot(PbsAvailable, "Live PBS not configured.");
        var volume = Environment.GetEnvironmentVariable("IMG_VOLUME"); // e.g. "X:"
        Skip.If(string.IsNullOrEmpty(volume), "IMG_VOLUME not set (no test volume prepared).");

        byte[] source;
        using (var snap = VssSnapshotSet.Create(new[] { volume! }))
        {
            var device = snap.DeviceForVolume(volume!);
            _out.WriteLine($"shadow device: {device}");
            using var raw = new RawVolume(device);
            _out.WriteLine($"volume length: {raw.Length}");

            // Read what the shadow device actually yields (can be slightly under the IOCTL length).
            using var buffer = new MemoryStream();
            await raw.Stream.CopyToAsync(buffer);
            source = buffer.ToArray();
            _out.WriteLine($"bytes read: {source.Length}");
            snap.Complete();
        }

        // It really is a volume image: NTFS boot sector signature at offset 3.
        Assert.Equal("NTFS", Encoding.ASCII.GetString(source, 3, 4));
        var sourceHash = Convert.ToHexString(SHA256.HashData(source));

        const string archive = "disk.img.fidx";
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = $"vssimg-{time}";
        DynamicArchive.UploadStats stats;
        await using (var s = await BackupSession.OpenAsync(Options(), "host", id, time))
        {
            stats = await FixedImage.UploadAsync(s, archive, new MemoryStream(source), source.Length);
            await s.FinishAsync();
        }
        _out.WriteLine($"image stats: {stats}"); // mostly-zero free space dedups heavily

        await using var reader = await ReaderSession.OpenAsync(Options(), "host", id, time);
        using var restored = new MemoryStream(source.Length);
        var written = await reader.RestoreFixedArchiveAsync(archive, restored);
        Assert.Equal(source.Length, written);
        Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(restored.ToArray())));
    }
}
