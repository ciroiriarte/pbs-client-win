using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pbs.Abstractions;

namespace Pbs.Protocol;

/// <summary>
/// A PBS backup session over the upgraded <c>proxmox-backup-protocol-v1</c> connection (see
/// <see cref="PbsUpgrade"/>). Endpoints reached over the connection (all relative to root):
/// <list type="bullet">
/// <item><c>POST dynamic_index</c> {archive-name} → wid</item>
/// <item><c>POST dynamic_chunk?wid&amp;digest&amp;size&amp;encoded-size</c> body=DataBlob</item>
/// <item><c>PUT dynamic_index</c> {wid, digest-list, offset-list}</item>
/// <item><c>POST dynamic_close</c> {wid, chunk-count, size, csum}</item>
/// <item><c>POST blob?encoded-size&amp;file-name</c> body=DataBlob (used for index.json.blob)</item>
/// <item><c>POST finish</c></item>
/// </list>
/// </summary>
public sealed class BackupSession : IAsyncDisposable
{
    public const string ProtocolId = "proxmox-backup-protocol-v1";
    public const string ManifestBlobName = "index.json.blob";

    private readonly HttpClient _http;
    private readonly List<ManifestFile> _files = new();
    private string? _keyFingerprint;
    private CryptConfig? _crypt;

    public string BackupType { get; }
    public string BackupId { get; }
    public long BackupTime { get; }

    private BackupSession(HttpClient http, string type, string id, long time)
    {
        _http = http;
        BackupType = type;
        BackupId = id;
        BackupTime = time;
    }

    /// <summary>Open a backup session, performing the protocol upgrade against the server.</summary>
    public static async Task<BackupSession> OpenAsync(
        PbsConnectionOptions options, string backupType, string backupId, long backupTimeUnix,
        bool benchmark = false, CancellationToken ct = default)
    {
        // benchmark=true opens the server's throughput-test mode: uploaded chunks are discarded and
        // no snapshot is persisted (used by the `benchmark` command for the TLS leg).
        var http = await PbsUpgrade.ConnectAsync(
            options, "/api2/json/backup", ProtocolId, backupType, backupId, backupTimeUnix,
            warmupPath: benchmark ? null : "previous_backup_time", benchmark: benchmark, ct: ct);
        return new BackupSession(http, backupType, backupId, backupTimeUnix);
    }

    /// <summary>
    /// Download the previous snapshot's index for <paramref name="archiveName"/> (raw <c>.didx</c>),
    /// or null when the group has no previous backup. As a side effect PBS registers the referenced
    /// chunks as "known", so a subsequent index may reference them without re-uploading their data.
    /// </summary>
    public async Task<byte[]?> DownloadPreviousIndexAsync(string archiveName, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"previous?archive-name={Uri.EscapeDataString(archiveName)}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode is >= 400 and < 500) return null; // no previous backup / archive
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"GET previous -> {(int)resp.StatusCode}: {text}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Backup time (unix seconds) of the group's previous snapshot — the one <c>previous?…</c>
    /// downloads refer to — or null for the group's first backup.
    /// </summary>
    public async Task<long?> GetPreviousBackupTimeAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "previous_backup_time")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET previous_backup_time -> {(int)resp.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number
            ? data.GetInt64()
            : null;
    }

    /// <summary>
    /// The previous snapshot's manifest JSON (decoded <c>index.json.blob</c>), or null when there is
    /// none. Used to check that a metadata-mode reference uses the same encryption mode and key.
    /// </summary>
    public async Task<JsonDocument?> DownloadPreviousManifestAsync(CancellationToken ct = default)
    {
        var blob = await DownloadPreviousIndexAsync(ManifestBlobName, ct);
        return blob is null ? null : JsonDocument.Parse(DataBlob.Decode(blob));
    }

    /// <summary>Create a dynamic (content-defined) index writer for <paramref name="archiveName"/> (e.g. <c>root.pxar.didx</c>).</summary>
    public async Task<long> CreateDynamicIndexAsync(string archiveName, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("dynamic_index", new Dictionary<string, object> { ["archive-name"] = archiveName }, ct);
        return data.GetInt64();
    }

    /// <summary>Upload one dynamic chunk (idempotent server-side by digest). Body is a DataBlob.</summary>
    public async Task UploadDynamicChunkAsync(long wid, ChunkRef chunk, byte[] blob, CancellationToken ct = default)
    {
        var q = $"dynamic_chunk?wid={wid}&digest={chunk.Digest.ToHex()}&size={chunk.Length}&encoded-size={blob.Length}";
        await PostBinaryAsync(q, blob, ct);
    }

    /// <summary>
    /// Register the ordered chunk list into the dynamic index. PBS validates each entry's
    /// <em>start</em> offset against its running position and derives the chunk size from the
    /// digest it already recorded during upload, so we send start offsets here.
    /// </summary>
    public async Task AppendDynamicIndexAsync(long wid, IReadOnlyList<ChunkRef> chunks, CancellationToken ct = default)
    {
        var digestList = new List<string>(chunks.Count);
        var offsetList = new List<long>(chunks.Count);
        foreach (var c in chunks)
        {
            digestList.Add(c.Digest.ToHex());
            offsetList.Add(c.Offset);
        }
        await PostJsonAsync("dynamic_index", new Dictionary<string, object>
        {
            ["wid"] = wid,
            ["digest-list"] = digestList,
            ["offset-list"] = offsetList,
        }, ct, HttpMethod.Put);
    }

    /// <summary>Record the key fingerprint stored in the manifest for encrypted backups.</summary>
    public void SetKeyFingerprint(string fingerprint) => _keyFingerprint = fingerprint;

    /// <summary>
    /// Provide the encryption key for an encrypted backup: records its fingerprint (stored in the
    /// manifest's <c>unprotected</c> block) and enables manifest signing (#6) at <see cref="FinishAsync"/>.
    /// </summary>
    public void SetCrypt(CryptConfig crypt)
    {
        ArgumentNullException.ThrowIfNull(crypt);
        _crypt = crypt;
        _keyFingerprint = crypt.FingerprintHex();
    }

    /// <summary>Close a dynamic index and record its file in the manifest.</summary>
    public async Task CloseDynamicIndexAsync(string archiveName, long wid, long chunkCount, long size, ChunkDigest indexCsum, string cryptMode = "none", CancellationToken ct = default)
    {
        await PostJsonAsync("dynamic_close", new Dictionary<string, object>
        {
            ["wid"] = wid,
            ["chunk-count"] = chunkCount,
            ["size"] = size,
            ["csum"] = indexCsum.ToHex(),
        }, ct);
        _files.Add(new ManifestFile(archiveName, cryptMode, size, indexCsum.ToHex()));
    }

    /// <summary>
    /// Create a fixed-size index writer for an image archive (e.g. <c>disk.img.fidx</c>), declaring
    /// the total image <paramref name="size"/> in bytes. Returns the writer id.
    /// </summary>
    public async Task<long> CreateFixedIndexAsync(string archiveName, long size, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("fixed_index", new Dictionary<string, object>
        {
            ["archive-name"] = archiveName,
            ["size"] = size,
        }, ct);
        return data.GetInt64();
    }

    /// <summary>Upload one fixed chunk (idempotent server-side by digest). Body is a DataBlob.</summary>
    public async Task UploadFixedChunkAsync(long wid, ChunkRef chunk, byte[] blob, CancellationToken ct = default)
    {
        var q = $"fixed_chunk?wid={wid}&digest={chunk.Digest.ToHex()}&size={chunk.Length}&encoded-size={blob.Length}";
        await PostBinaryAsync(q, blob, ct);
    }

    /// <summary>Register the ordered chunk list (start byte offsets + digests) into the fixed index.</summary>
    public async Task AppendFixedIndexAsync(long wid, IReadOnlyList<ChunkRef> chunks, CancellationToken ct = default)
    {
        var digestList = new List<string>(chunks.Count);
        var offsetList = new List<long>(chunks.Count);
        foreach (var c in chunks)
        {
            digestList.Add(c.Digest.ToHex());
            offsetList.Add(c.Offset);
        }
        await PostJsonAsync("fixed_index", new Dictionary<string, object>
        {
            ["wid"] = wid,
            ["digest-list"] = digestList,
            ["offset-list"] = offsetList,
        }, ct, HttpMethod.Put);
    }

    /// <summary>Close a fixed index and record its file in the manifest.</summary>
    public async Task CloseFixedIndexAsync(string archiveName, long wid, long chunkCount, long size, ChunkDigest indexCsum, string cryptMode = "none", CancellationToken ct = default)
    {
        await PostJsonAsync("fixed_close", new Dictionary<string, object>
        {
            ["wid"] = wid,
            ["chunk-count"] = chunkCount,
            ["size"] = size,
            ["csum"] = indexCsum.ToHex(),
        }, ct);
        _files.Add(new ManifestFile(archiveName, cryptMode, size, indexCsum.ToHex()));
    }

    /// <summary>Upload a standalone blob (raw DataBlob body) under <paramref name="fileName"/>.</summary>
    public async Task UploadBlobAsync(string fileName, byte[] blob, CancellationToken ct = default)
    {
        var q = $"blob?encoded-size={blob.Length}&file-name={Uri.EscapeDataString(fileName)}";
        await PostBinaryAsync(q, blob, ct);
    }

    /// <summary>Build+upload the manifest (index.json.blob) from the closed archives, then POST finish.</summary>
    public async Task FinishAsync(CancellationToken ct = default)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["backup-type"] = BackupType,
            ["backup-id"] = BackupId,
            ["backup-time"] = BackupTime,
            ["files"] = _files.Select(f => new Dictionary<string, object?>
            {
                ["filename"] = f.FileName,
                ["crypt-mode"] = f.CryptMode,
                ["size"] = f.Size,
                ["csum"] = f.Csum,
            }).ToList(),
            ["unprotected"] = _keyFingerprint is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["key-fingerprint"] = _keyFingerprint },
        };

        // Sign the manifest (#6) when encrypted: HMAC-SHA256(id_key, canonical-json of the protected
        // fields), matching upstream — the signature is over the manifest with "unprotected" and
        // "signature" excluded, so it authenticates backup-type/id/time + the files list. The official
        // client verifies this on restore with the same key.
        if (_crypt is not null)
        {
            var signed = new Dictionary<string, object?>
            {
                ["backup-type"] = manifest["backup-type"],
                ["backup-id"] = manifest["backup-id"],
                ["backup-time"] = manifest["backup-time"],
                ["files"] = manifest["files"],
            };
            var sig = _crypt.ComputeAuthTag(CanonicalJson.Serialize(signed));
            manifest["signature"] = Convert.ToHexString(sig).ToLowerInvariant();
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        var blob = DataBlob.Encode(json, compress: true);
        await UploadBlobAsync(ManifestBlobName, blob, ct);

        await PostJsonAsync("finish", new Dictionary<string, object>(), ct);
    }

    private async Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct, HttpMethod? method = null)
    {
        using var req = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact, // reuse the single upgraded h2 connection
        };
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{req.Method} {path} -> {(int)resp.StatusCode}: {text}");
        if (string.IsNullOrWhiteSpace(text)) return default;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    private async Task PostBinaryAsync(string path, byte[] body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact, // reuse the single upgraded h2 connection
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"POST {path} -> {(int)resp.StatusCode}: {text}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct ManifestFile(string FileName, string CryptMode, long Size, string Csum);
}
