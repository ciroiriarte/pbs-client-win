using System.Runtime.Versioning;
using Pbs.Abstractions;

namespace Pbs.Vss;

/// <summary>
/// <see cref="ISnapshotSource"/> backed by a VSS snapshot of the requested volumes. Reading a live
/// path via <see cref="Map"/> returns its shadow-copy path.
///
/// A system-wide named semaphore serializes our VSS backups so two of our own instances cannot
/// collide with VSS_E_SNAPSHOT_SET_IN_PROGRESS. A semaphore, not a Mutex, is used because the acquire
/// and release happen across <c>await</c> points on different threads, which a Mutex's thread affinity
/// would reject. The semaphore cannot see <em>other</em> backup software (a PVE host-level backup
/// driving VSS via the QEMU guest agent, or an application's own VSS writer); overlaps with those still
/// surface as VSS_E_SNAPSHOT_SET_IN_PROGRESS, so snapshot creation is wrapped in <see cref="VssBusyRetry"/>
/// (bounded exponential back-off). See docs/VSS-COORDINATION.md.
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
            // Own-instance collisions are already excluded by the semaphore; retry only covers external
            // contention (another requestor's snapshot set in progress), which the semaphore can't see.
            _set = VssBusyRetry.Run(() => VssSnapshotSet.Create(volumes));
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
