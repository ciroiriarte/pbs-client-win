namespace Pbs.Protocol;

using System.Net.Http.Headers;

/// <summary>Everything needed to reach a PBS datastore: endpoint, auth, pinning, target store.</summary>
public sealed class PbsConnectionOptions
{
    /// <summary>Base URL of the PBS API, e.g. <c>https://pbs.example.com:8007/</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>API token used for authentication.</summary>
    public PbsApiToken? Token { get; init; }

    /// <summary>Ticket/cookie authentication returned by <see cref="PbsAuth.LoginAsync"/>.</summary>
    public PbsTicket? Ticket { get; init; }

    /// <summary>Pinned server certificate fingerprint. When null, the platform trust chain is used.</summary>
    public CertificateFingerprint? Fingerprint { get; init; }

    /// <summary>Target datastore name.</summary>
    public string? Datastore { get; init; }

    /// <summary>Optional datastore namespace (empty = root namespace).</summary>
    public string? Namespace { get; init; }

    /// <summary>Apply the configured authentication to HTTP request headers.</summary>
    public void ApplyAuth(HttpRequestHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (Token is { } token)
        {
            headers.Authorization = new AuthenticationHeaderValue("PBSAPIToken", $"{token.TokenId}:{token.Secret}");
            return;
        }

        if (Ticket is { } ticket)
        {
            headers.TryAddWithoutValidation("Cookie", $"PBSAuthCookie={ticket.Ticket}");
            headers.TryAddWithoutValidation("CSRFPreventionToken", ticket.CsrfToken);
            return;
        }

        throw new InvalidOperationException("connection requires API token or ticket authentication");
    }

    /// <summary>Raw HTTP/1.1 authentication header lines for the PBS upgrade handshake.</summary>
    public IEnumerable<string> AuthHeaderLines()
    {
        if (Token is { } token)
        {
            yield return $"Authorization: PBSAPIToken {token.TokenId}:{token.Secret}\r\n";
            yield break;
        }

        if (Ticket is { } ticket)
        {
            yield return $"Cookie: PBSAuthCookie={ticket.Ticket}\r\n";
            yield return $"CSRFPreventionToken: {ticket.CsrfToken}\r\n";
            yield break;
        }

        throw new InvalidOperationException("connection requires API token or ticket authentication");
    }
}
