using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pbs.Protocol;

/// <summary>PBS <c>/version</c> response payload.</summary>
public sealed record PbsVersion(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("release")] string? Release,
    [property: JsonPropertyName("repoid")] string? RepoId);

/// <summary>
/// Client for the PBS JSON API (the request/response side over HTTP/2). The streaming backup and
/// reader protocols (connection upgrade to <c>proxmox-backup-protocol-v1</c>) build on top of this
/// and are added as they are verified against a live server.
/// </summary>
public sealed class PbsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>The connection this client targets.</summary>
    public PbsConnectionOptions Options { get; }

    // Test seam: inject a preconfigured HttpClient (e.g. with a stub handler).
    internal PbsClient(HttpClient http, PbsConnectionOptions options, bool ownsHttp)
    {
        _http = http;
        Options = options;
        _ownsHttp = ownsHttp;
    }

    /// <summary>Create a client with certificate pinning and API-token auth wired up.</summary>
    public static PbsClient Create(PbsConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (options.Fingerprint is { } pinned)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                cert is not null && pinned.Matches(cert);
        }

        var http = new HttpClient(handler)
        {
            BaseAddress = Normalize(options.BaseUrl),
            // Prefer HTTP/2 (required by the backup/reader session) but fall back to HTTP/1.1 for
            // the plain JSON API, which PBS may serve over 1.1 depending on ALPN negotiation.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("PBSAPIToken", $"{options.Token.TokenId}:{options.Token.Secret}");

        return new PbsClient(http, options, ownsHttp: true);
    }

    /// <summary>GET <c>/api2/json/version</c>.</summary>
    public async Task<PbsVersion> GetVersionAsync(CancellationToken ct = default)
    {
        var envelope = await _http.GetFromJsonAsync<ApiEnvelope<PbsVersion>>("api2/json/version", ct)
                       ?? throw new InvalidDataException("empty response from /version");
        return envelope.Data ?? throw new InvalidDataException("missing data in /version response");
    }

    /// <summary>
    /// List snapshots in the configured datastore/namespace, optionally filtered by backup id.
    /// Returns the raw JSON array elements from <c>/admin/datastore/{store}/snapshots</c>.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> ListSnapshotsAsync(string? backupId = null, CancellationToken ct = default)
    {
        var store = Options.Datastore ?? throw new InvalidOperationException("datastore required");
        var path = $"api2/json/admin/datastore/{Uri.EscapeDataString(store)}/snapshots";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(Options.Namespace)) query.Add("ns=" + Uri.EscapeDataString(Options.Namespace));
        if (!string.IsNullOrEmpty(backupId)) query.Add("backup-id=" + Uri.EscapeDataString(backupId));
        if (query.Count > 0) path += "?" + string.Join('&', query);

        var envelope = await _http.GetFromJsonAsync<ApiEnvelope<JsonElement>>(path, ct);
        if (envelope?.Data is not { ValueKind: JsonValueKind.Array } arr) return Array.Empty<JsonElement>();
        return arr.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static Uri Normalize(Uri baseUrl)
    {
        var s = baseUrl.AbsoluteUri;
        return s.EndsWith('/') ? baseUrl : new Uri(s + "/");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>PBS wraps all JSON API results in a <c>{ "data": … }</c> envelope.</summary>
    private sealed record ApiEnvelope<T>([property: JsonPropertyName("data")] T? Data);
}
