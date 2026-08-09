using System.Runtime.Versioning;
using System.Security.Cryptography;
using Pbs.Cli;
using Pbs.Protocol;
using Pbs.Pxar;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Vss.Tests;

/// <summary>
/// The full Windows path, end to end against a live PBS: take a VSS snapshot, encode the directory
/// from the point-in-time shadow copy as pxar, back it up over the protocol, then restore and
/// verify — crucially including a file that is held under an exclusive lock (unreadable live).
/// </summary>
[SupportedOSPlatform("windows")]
public class VssBackupE2ETests
{
    private readonly ITestOutputHelper _out;
    public VssBackupE2ETests(ITestOutputHelper output) => _out = output;

    private static bool PbsAvailable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PBS_ENDPOINT")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PBS_TOKEN_ID")) &&
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
    public async Task VssSnapshot_BackupToPbs_Restore_IncludingLockedFile()
    {
        Skip.IfNot(PbsAvailable, "Live PBS not configured.");

        var dir = Path.Combine(Path.GetTempPath(), $"vss-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "normal.txt"), "a normal file\n");
        var big = new byte[128 * 1024];
        new Random(7).NextBytes(big);
        File.WriteAllBytes(Path.Combine(dir, "sub", "data.bin"), big);

        // A file held OPEN with an exclusive write lock — unreadable on the live volume.
        var lockedPath = Path.Combine(dir, "locked.db");
        var lockedContent = "consistent-point-in-time-" + Guid.NewGuid();
        File.WriteAllText(lockedPath, lockedContent);
        using var lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Sanity: the live file really is unreadable while locked.
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(lockedPath));

        var expected = HashTree(dir, exclude: lockedPath);

        try
        {
            // 1. VSS snapshot, then encode the directory FROM THE SHADOW COPY.
            byte[] pxarBytes;
            using (var snap = VssSnapshotSet.Create(new[] { Path.GetPathRoot(dir)! }))
            {
                var shadowDir = snap.MapPath(dir);
                _out.WriteLine($"shadow dir: {shadowDir}");
                using var ms = new MemoryStream();
                await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(shadowDir), ms);
                pxarBytes = ms.ToArray();
                snap.Complete();
            }
            _out.WriteLine($"pxar bytes: {pxarBytes.Length}");

            // 2. Back up to PBS.
            const string archive = "root.pxar.didx";
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var id = $"vss-e2e-{time}";
            await using (var s = await BackupSession.OpenAsync(Options(), "host", id, time))
            {
                await DynamicArchive.UploadAsync(s, archive, pxarBytes);
                await s.FinishAsync();
            }

            // 3. Restore the archive and extract it.
            var restoreDir = Path.Combine(Path.GetTempPath(), $"vss-e2e-restore-{Guid.NewGuid():N}");
            await using (var reader = await ReaderSession.OpenAsync(Options(), "host", id, time))
            {
                using var buffer = new MemoryStream();
                await reader.RestoreDynamicArchiveAsync(archive, buffer);
                buffer.Position = 0;
                await PxarDecoder.ExtractAsync(buffer, restoreDir);
            }

            // 4. The locked file was captured consistently and restored with correct content.
            var restoredLocked = Path.Combine(restoreDir, "locked.db");
            Assert.True(File.Exists(restoredLocked), "locked file missing from VSS backup");
            Assert.Equal(lockedContent, File.ReadAllText(restoredLocked));

            // ...and the rest of the tree matches.
            var restored = HashTree(restoreDir, exclude: restoredLocked);
            Assert.Equal(expected, restored);

            Directory.Delete(restoreDir, recursive: true);
        }
        finally
        {
            lockHandle.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static SortedDictionary<string, string> HashTree(string root, string exclude)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(exclude), StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            result[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        }
        return result;
    }
}
