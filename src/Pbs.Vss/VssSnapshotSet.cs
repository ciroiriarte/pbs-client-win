using System.Runtime.Versioning;
using Alphaleonis.Win32.Vss;

namespace Pbs.Vss;

/// <summary>
/// A Volume Shadow Copy Service snapshot of one or more volumes, taken as a backup requestor.
/// While the set is alive, files can be read from the point-in-time shadow copy via
/// <see cref="MapPath"/> even if they are locked/open on the live volume. Disposing deletes the
/// snapshot set. Requires administrator/SYSTEM privileges.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VssSnapshotSet : IDisposable
{
    private readonly IVssBackupComponents _bc;
    private readonly Guid _setId;
    private readonly Dictionary<string, string> _deviceByVolume = new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    private VssSnapshotSet(IVssBackupComponents bc, Guid setId)
    {
        _bc = bc;
        _setId = setId;
    }

    /// <summary>Create an application/file-consistent snapshot of the given volumes (e.g. "C:\\").</summary>
    public static VssSnapshotSet Create(IEnumerable<string> volumes)
    {
        var factory = VssFactoryProvider.Default.GetVssFactory();
        var bc = factory.CreateVssBackupComponents();
        try
        {
            bc.InitializeForBackup(null);
            bc.SetContext(VssSnapshotContext.Backup);
            bc.SetBackupState(false, false, VssBackupType.Full, false);
            bc.GatherWriterMetadata(); // required before PrepareForBackup, else VSS_E_BAD_STATE

            var vols = volumes.Select(NormalizeVolume).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var setId = bc.StartSnapshotSet();
            var snapIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in vols)
                snapIds[v] = bc.AddToSnapshotSet(v, Guid.Empty);

            bc.PrepareForBackup();
            bc.DoSnapshotSet();

            var set = new VssSnapshotSet(bc, setId);
            foreach (var v in vols)
            {
                var props = bc.GetSnapshotProperties(snapIds[v]);
                set._deviceByVolume[v] = props.SnapshotDeviceObject;
            }
            return set;
        }
        catch
        {
            bc.Dispose();
            throw;
        }
    }

    /// <summary>The shadow-copy device path for a volume (\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN).</summary>
    public string DeviceForVolume(string volume) => _deviceByVolume[NormalizeVolume(volume)];

    /// <summary>Volumes captured in this snapshot set.</summary>
    public IReadOnlyCollection<string> Volumes => _deviceByVolume.Keys;

    /// <summary>Map a live full path (e.g. <c>C:\dir\file</c>) to its path inside the shadow copy.</summary>
    public string MapPath(string fullPath)
    {
        var root = NormalizeVolume(Path.GetPathRoot(fullPath) ?? throw new ArgumentException($"no volume root in '{fullPath}'"));
        if (!_deviceByVolume.TryGetValue(root, out var device))
            throw new InvalidOperationException($"volume '{root}' is not part of this snapshot set");
        var relative = fullPath[root.Length..].TrimStart('\\');
        return device + "\\" + relative;
    }

    /// <summary>Signal successful backup so writers can finalize (e.g. truncate logs).</summary>
    public void Complete()
    {
        if (_completed) return;
        _bc.BackupComplete();
        _completed = true;
    }

    private static string NormalizeVolume(string v)
    {
        var trimmed = v.TrimEnd('\\', '/');
        return trimmed + "\\"; // "C:" / "C:\" / "C:\\" -> "C:\"
    }

    public void Dispose()
    {
        try
        {
            if (!_completed)
            {
                try { _bc.BackupComplete(); } catch { /* best effort */ }
            }
            _bc.DeleteSnapshotSet(_setId, false);
        }
        catch { /* best effort cleanup */ }
        finally
        {
            _bc.Dispose();
        }
    }
}
