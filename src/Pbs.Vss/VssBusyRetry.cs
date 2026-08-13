using System.Runtime.Versioning;

namespace Pbs.Vss;

/// <summary>
/// Retries a VSS operation that fails because a shadow-copy set is already in progress elsewhere on
/// the system. <see cref="VssSnapshotSource"/>'s own-instance semaphore prevents two of <em>our</em>
/// backups from colliding, but it cannot see other requestors — a PVE host-level backup driving VSS
/// through the QEMU guest agent's fs-freeze, or an application's own VSS writer. Those surface as
/// <c>VSS_E_SNAPSHOT_SET_IN_PROGRESS</c> (HRESULT <c>0x80042316</c>) from <c>DoSnapshotSet</c>; we back
/// off and retry rather than failing the whole backup on a transient overlap. See docs/VSS-COORDINATION.md.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VssBusyRetry
{
    /// <summary>HRESULT <c>VSS_E_SNAPSHOT_SET_IN_PROGRESS</c> — another shadow copy operation is in progress.</summary>
    public const int SnapshotSetInProgressHResult = unchecked((int)0x80042316);

    /// <summary>
    /// True when <paramref name="ex"/> indicates VSS is busy with another snapshot set. Detected by the
    /// VSS HRESULT and, as a fallback, by the AlphaVSS typed-exception name, so it works whether the
    /// library propagates the raw HRESULT or a <c>VssSnapshotSetInProgressException</c>.
    /// </summary>
    public static bool IsBusy(Exception ex) =>
        ex.HResult == SnapshotSetInProgressHResult ||
        ex.GetType().Name.Contains("SnapshotSetInProgress", StringComparison.Ordinal);

    /// <summary>
    /// Run <paramref name="op"/>, retrying only on a "snapshot set in progress" failure with exponential
    /// back-off (<paramref name="initialDelay"/> doubling up to <paramref name="maxDelay"/>) until
    /// <paramref name="maxAttempts"/> is reached, after which the last busy exception propagates. Any
    /// non-busy exception propagates immediately. <paramref name="sleep"/> is injectable for tests.
    /// </summary>
    public static T Run<T>(
        Func<T> op,
        int maxAttempts = 5,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var delay = initialDelay ?? TimeSpan.FromSeconds(2);
        var cap = maxDelay ?? TimeSpan.FromSeconds(30);
        sleep ??= Thread.Sleep;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return op();
            }
            catch (Exception ex) when (IsBusy(ex) && attempt < maxAttempts)
            {
                sleep(delay);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, cap.Ticks));
            }
        }
    }
}
