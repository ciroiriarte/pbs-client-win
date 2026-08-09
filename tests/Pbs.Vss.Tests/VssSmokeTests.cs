using System.Runtime.Versioning;
using Pbs.Vss;
using Xunit;

namespace Pbs.Vss.Tests;

/// <summary>
/// Exercises real VSS on the build VM (SYSTEM/admin). Snapshots C:, then reads a just-written
/// marker file from the point-in-time shadow copy — proving the snapshot + path mapping work.
/// </summary>
[SupportedOSPlatform("windows")]
public class VssSmokeTests
{
    [Fact]
    public void Snapshot_CVolume_MarkerReadableFromShadowCopy()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"vss-marker-{Guid.NewGuid():N}.txt");
        var content = Guid.NewGuid().ToString();
        File.WriteAllText(marker, content);
        try
        {
            using var snap = VssSnapshotSet.Create(new[] { "C:\\" });

            var shadowPath = snap.MapPath(marker);
            Assert.NotEqual(marker, shadowPath);                     // it maps to the device path
            Assert.True(File.Exists(shadowPath), $"marker missing in shadow copy: {shadowPath}");
            Assert.Equal(content, File.ReadAllText(shadowPath));     // point-in-time contents match

            snap.Complete();
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void OpenLockedFile_IsStillReadableViaSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vss-locked-{Guid.NewGuid():N}.txt");
        var content = "locked-but-snapshotted";
        File.WriteAllText(path, content);
        // Hold the file open with an exclusive write lock (no sharing) to mimic an open DB/registry.
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        try
        {
            using var snap = VssSnapshotSet.Create(new[] { "C:\\" });
            var shadowPath = snap.MapPath(path);
            Assert.Equal(content, File.ReadAllText(shadowPath));     // readable despite the live lock
            snap.Complete();
        }
        finally
        {
            locked.Dispose();
            File.Delete(path);
        }
    }
}
