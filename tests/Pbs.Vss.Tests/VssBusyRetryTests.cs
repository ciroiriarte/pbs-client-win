using System.Runtime.Versioning;
using Pbs.Vss;
using Xunit;

namespace Pbs.Vss.Tests;

/// <summary>
/// Unit tests for the external-contention back-off (#8). Pure logic — no real VSS — so it runs on any
/// Windows agent. <c>sleep</c> is stubbed and delays are zero so the tests are instant.
/// </summary>
[SupportedOSPlatform("windows")]
public class VssBusyRetryTests
{
    private static Exception Busy() => new InvalidOperationException("snapshot set in progress")
    {
        HResult = VssBusyRetry.SnapshotSetInProgressHResult,
    };

    [Fact]
    public void IsBusy_MatchesOnHResult()
    {
        Assert.True(VssBusyRetry.IsBusy(Busy()));
        Assert.False(VssBusyRetry.IsBusy(new InvalidOperationException("unrelated")));
    }

    [Fact]
    public void IsBusy_MatchesOnTypeName()
    {
        Assert.True(VssBusyRetry.IsBusy(new VssSnapshotSetInProgressException()));
    }

    [Fact]
    public void Run_RetriesBusyThenSucceeds()
    {
        var calls = 0;
        var result = VssBusyRetry.Run(
            () =>
            {
                calls++;
                if (calls < 3) throw Busy();
                return 42;
            },
            maxAttempts: 5,
            initialDelay: TimeSpan.Zero,
            sleep: _ => { });

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Run_GivesUpAfterMaxAttempts_PropagatingBusy()
    {
        var calls = 0;
        var ex = Assert.Throws<InvalidOperationException>(() => VssBusyRetry.Run<int>(
            () => { calls++; throw Busy(); },
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero,
            sleep: _ => { }));

        Assert.Equal(3, calls);            // tried exactly maxAttempts times
        Assert.True(VssBusyRetry.IsBusy(ex));
    }

    [Fact]
    public void Run_DoesNotRetryNonBusy()
    {
        var calls = 0;
        Assert.Throws<TimeoutException>(() => VssBusyRetry.Run<int>(
            () => { calls++; throw new TimeoutException("other failure"); },
            maxAttempts: 5,
            sleep: _ => { }));

        Assert.Equal(1, calls);            // failed fast, no retry
    }

    [Fact]
    public void Run_BackoffDoublesAndIsCapped()
    {
        var delays = new List<TimeSpan>();
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => VssBusyRetry.Run<int>(
            () => { calls++; throw Busy(); },
            maxAttempts: 5,
            initialDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(3),
            sleep: delays.Add));

        // 4 sleeps between 5 attempts: 1s, 2s, capped 3s, capped 3s.
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 3.0 }, delays.Select(d => d.TotalSeconds));
    }

    // The real AlphaVSS exception type name must contain "SnapshotSetInProgress" for the name-based
    // fallback in IsBusy to hold; a tiny local stand-in mirrors that shape without needing real VSS.
    private sealed class VssSnapshotSetInProgressException : Exception;
}
