using Xunit;

namespace Pbs.Integration.Tests;

/// <summary>
/// End-to-end checks against a real PBS server: exercises API-token auth, TLS fingerprint pinning,
/// and HTTP/2 — the plumbing that unit tests can only stub.
/// </summary>
public class ConnectivityTests
{
    [SkippableFact]
    public async Task GetVersion_AgainstLiveServer()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        using var client = PbsTestServer.CreateClient();
        var version = await client.GetVersionAsync();

        Assert.False(string.IsNullOrWhiteSpace(version.Version));
    }
}
