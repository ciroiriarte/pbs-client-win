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

    /// <summary>
    /// Create a namespace in the datastore. <paramref name="fullNamespace"/> may be nested
    /// (<c>a/b/c</c>); the parent is created-into and the final component is the new namespace.
    /// </summary>
    public async Task CreateNamespaceAsync(string fullNamespace, CancellationToken ct = default)
    {
        var trimmed = fullNamespace.Trim('/');
        int slash = trimmed.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : trimmed[..slash];
        var name = slash < 0 ? trimmed : trimmed[(slash + 1)..];

        var body = new Dictionary<string, object?> { ["name"] = name };
        if (parent.Length > 0) body["parent"] = parent;
        using var resp = await _http.PostAsJsonAsync($"api2/json/admin/datastore/{Store()}/namespace", body, ct);
        await EnsureOkAsync(resp, "namespace create", ct);
    }

    /// <summary>Datastore status (total/used/available bytes, etc.) as the raw JSON object.</summary>
    public async Task<JsonElement> GetStatusAsync(CancellationToken ct = default)
    {
        var env = await _http.GetFromJsonAsync<ApiEnvelope<JsonElement>>($"api2/json/admin/datastore/{Store()}/status", ct);
        return env?.Data ?? default;
    }

    /// <summary>List namespaces in the datastore.</summary>
    public async Task<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken ct = default)
    {
        var env = await _http.GetFromJsonAsync<ApiEnvelope<JsonElement>>($"api2/json/admin/datastore/{Store()}/namespace", ct);
        if (env?.Data is not { ValueKind: JsonValueKind.Array } arr) return Array.Empty<string>();
        return arr.EnumerateArray().Select(e => e.GetProperty("ns").GetString() ?? string.Empty).ToList();
    }

    /// <summary>Delete a namespace (optionally including all its backup groups).</summary>
    public async Task DeleteNamespaceAsync(string ns, bool deleteGroups, CancellationToken ct = default)
    {
        var q = $"ns={Uri.EscapeDataString(ns)}&delete-groups={(deleteGroups ? "true" : "false")}";
        using var resp = await _http.DeleteAsync($"api2/json/admin/datastore/{Store()}/namespace?{q}", ct);
        await EnsureOkAsync(resp, "namespace delete", ct);
    }

    /// <summary>Mark a snapshot protected (or not) — protected snapshots are skipped by prune/GC.</summary>
    public async Task SetProtectedAsync(string backupType, string backupId, long backupTime, bool isProtected, CancellationToken ct = default)
    {
        var body = SnapshotBody(backupType, backupId, backupTime);
        body["protected"] = isProtected;
        using var resp = await _http.PutAsJsonAsync($"api2/json/admin/datastore/{Store()}/protected", body, ct);
        await EnsureOkAsync(resp, "set protected", ct);
    }

    /// <summary>Delete (forget) a single snapshot.</summary>
    public async Task ForgetSnapshotAsync(string backupType, string backupId, long backupTime, CancellationToken ct = default)
    {
        var q = SnapshotQuery(backupType, backupId, backupTime);
        using var resp = await _http.DeleteAsync($"api2/json/admin/datastore/{Store()}/snapshots?{q}", ct);
        await EnsureOkAsync(resp, "forget", ct);
    }

    /// <summary>Get the notes attached to a snapshot (empty string if none).</summary>
    public async Task<string> GetNotesAsync(string backupType, string backupId, long backupTime, CancellationToken ct = default)
    {
        var q = SnapshotQuery(backupType, backupId, backupTime);
        var env = await _http.GetFromJsonAsync<ApiEnvelope<string>>($"api2/json/admin/datastore/{Store()}/notes?{q}", ct);
        return env?.Data ?? string.Empty;
    }

    /// <summary>Set the notes attached to a snapshot.</summary>
    public async Task SetNotesAsync(string backupType, string backupId, long backupTime, string notes, CancellationToken ct = default)
    {
        var body = SnapshotBody(backupType, backupId, backupTime);
        body["notes"] = notes;
        using var resp = await _http.PutAsJsonAsync($"api2/json/admin/datastore/{Store()}/notes", body, ct);
        await EnsureOkAsync(resp, "set notes", ct);
    }

    /// <summary>A single kept/removed entry from a prune run.</summary>
    public sealed record PruneEntry(long BackupTime, bool Keep);

    /// <summary>
    /// Prune a backup group by retention (<paramref name="keepOptions"/> keys like <c>keep-last</c>,
    /// <c>keep-daily</c>…). With <paramref name="dryRun"/> nothing is deleted; the returned list
    /// still reports what would be kept/removed.
    /// </summary>
    public async Task<IReadOnlyList<PruneEntry>> PruneGroupAsync(
        string backupType, string backupId, IReadOnlyDictionary<string, long> keepOptions, bool dryRun, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["backup-type"] = backupType,
            ["backup-id"] = backupId,
            ["dry-run"] = dryRun,
        };
        if (!string.IsNullOrEmpty(Options.Namespace)) body["ns"] = Options.Namespace;
        foreach (var (k, v) in keepOptions) body[k] = v;

        using var resp = await _http.PostAsJsonAsync($"api2/json/admin/datastore/{Store()}/prune", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"prune -> {(int)resp.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        var result = new List<PruneEntry>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var e in data.EnumerateArray())
                result.Add(new PruneEntry(e.GetProperty("backup-time").GetInt64(),
                    e.TryGetProperty("keep", out var keep) && keep.GetBoolean()));
        return result;
    }

    private string Store() => Uri.EscapeDataString(Options.Datastore ?? throw new InvalidOperationException("datastore required"));

    private string SnapshotQuery(string type, string id, long time)
    {
        var parts = new List<string>
        {
            "backup-type=" + Uri.EscapeDataString(type),
            "backup-id=" + Uri.EscapeDataString(id),
            "backup-time=" + time,
        };
        if (!string.IsNullOrEmpty(Options.Namespace)) parts.Add("ns=" + Uri.EscapeDataString(Options.Namespace));
        return string.Join('&', parts);
    }

    private Dictionary<string, object?> SnapshotBody(string type, string id, long time)
    {
        var body = new Dictionary<string, object?>
        {
            ["backup-type"] = type,
            ["backup-id"] = id,
            ["backup-time"] = time,
        };
        if (!string.IsNullOrEmpty(Options.Namespace)) body["ns"] = Options.Namespace;
        return body;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"{what} -> {(int)resp.StatusCode}: {text}");
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
