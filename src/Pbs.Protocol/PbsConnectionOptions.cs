namespace Pbs.Protocol;

/// <summary>Everything needed to reach a PBS datastore: endpoint, auth, pinning, target store.</summary>
public sealed class PbsConnectionOptions
{
    /// <summary>Base URL of the PBS API, e.g. <c>https://pbs.example.com:8007/</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>API token used for authentication.</summary>
    public required PbsApiToken Token { get; init; }

    /// <summary>Pinned server certificate fingerprint. When null, the platform trust chain is used.</summary>
    public CertificateFingerprint? Fingerprint { get; init; }

    /// <summary>Target datastore name.</summary>
    public string? Datastore { get; init; }

    /// <summary>Optional datastore namespace (empty = root namespace).</summary>
    public string? Namespace { get; init; }
}
