using System.Runtime.Versioning;
using Pbs.Abstractions;

namespace Pbs.Vss;

/// <summary>
/// <see cref="ISnapshotSource"/> backed by a VSS snapshot of the requested volumes. Reading a live
/// path via <see cref="Map"/> returns its shadow-copy path.
///
/// A system-wide named semaphore serializes our VSS backups so two of our own instances cannot
/// collide with VSS_E_SNAPSHOT_SET_IN_PROGRESS. (This does not coordinate with other backup software
/// — e.g. PVE host-level backups or SQL's own VSS writer — which is a broader design item.)
/// A semaphore, not a Mutex, is used because the acquire and release happen across <c>await</c>
/// points on different threads, which a Mutex's thread affinity would reject.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VssSnapshotSource : ISnapshotSource
{
    private const string GuardName = @"Global\PbsClientWin.Vss.SingleInstance";

    private readonly Semaphore _guard;
    private readonly VssSnapshotSet _set;
    private readonly List<RawVolume> _openVolumes = new();
    private bool _guardHeld;

    public VssSnapshotSource(IReadOnlyCollection<string> volumes, TimeSpan? acquireTimeout = null)
    {
        _guard = new Semaphore(1, 1, GuardName);
        if (!_guard.WaitOne(acquireTimeout ?? TimeSpan.FromSeconds(30)))
        {
            _guard.Dispose();
            throw new InvalidOperationException("another VSS backup is already in progress on this system");
        }
        _guardHeld = true;
        try
        {
            _set = VssSnapshotSet.Create(volumes);
        }
        catch
        {
            ReleaseGuard();
            _guard.Dispose();
            throw;
        }
    }

    public string Map(string livePath) => _set.MapPath(livePath);

    public (Stream Stream, long Size) OpenImage(string volume)
    {
        var raw = new RawVolume(_set.DeviceForVolume(volume));
        _openVolumes.Add(raw);
        return (raw.Stream, raw.Length);
    }

    public void Complete() => _set.Complete();

    public void Dispose()
    {
        foreach (var v in _openVolumes) { try { v.Dispose(); } catch { /* best effort */ } }
        try { _set.Dispose(); }
        finally
        {
            ReleaseGuard();
            _guard.Dispose();
        }
    }

    private void ReleaseGuard()
    {
        if (!_guardHeld) return;
        _guardHeld = false;
        try { _guard.Release(); } catch { /* best effort */ }
    }
}
