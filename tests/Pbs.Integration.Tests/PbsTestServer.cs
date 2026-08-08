using Pbs.Protocol;
using Xunit;

namespace Pbs.Integration.Tests;

/// <summary>
/// Reads live PBS connection details from environment variables so real-server integration tests
/// stay out of the repo. When the variables are absent (e.g. plain CI), <see cref="Available"/> is
/// false and tests short-circuit instead of failing. Populate via the session's pbs-test.env:
/// <c>PBS_ENDPOINT, PBS_TOKEN_ID, PBS_TOKEN_SECRET, PBS_FINGERPRINT, PBS_DATASTORE, PBS_NAMESPACE</c>.
/// </summary>
public static class PbsTestServer
{
    public static string? Endpoint => Environment.GetEnvironmentVariable("PBS_ENDPOINT");
    public static string? TokenId => Environment.GetEnvironmentVariable("PBS_TOKEN_ID");
    public static string? TokenSecret => Environment.GetEnvironmentVariable("PBS_TOKEN_SECRET");
    public static string? Fingerprint => Environment.GetEnvironmentVariable("PBS_FINGERPRINT");
    public static string Datastore => Environment.GetEnvironmentVariable("PBS_DATASTORE") ?? "backup01";
    public static string? Namespace => Environment.GetEnvironmentVariable("PBS_NAMESPACE");

    public static bool Available =>
        !string.IsNullOrEmpty(Endpoint) &&
        !string.IsNullOrEmpty(TokenId) &&
        !string.IsNullOrEmpty(TokenSecret);

    public const string SkipReason =
        "Live PBS server not configured (set PBS_ENDPOINT/PBS_TOKEN_ID/PBS_TOKEN_SECRET).";

    public static PbsConnectionOptions Options() => new()
    {
        BaseUrl = new Uri(Endpoint!),
        Token = new PbsApiToken(TokenId!, TokenSecret!),
        Fingerprint = string.IsNullOrEmpty(Fingerprint) ? null : CertificateFingerprint.Parse(Fingerprint),
        Datastore = Datastore,
        Namespace = Namespace,
    };

    public static PbsClient CreateClient() => PbsClient.Create(Options());
}
