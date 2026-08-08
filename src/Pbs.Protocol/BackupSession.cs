using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Pbs.Abstractions;

namespace Pbs.Protocol;

/// <summary>
/// A PBS backup session over the upgraded <c>proxmox-backup-protocol-v1</c> connection.
///
/// PBS starts a backup with an HTTP/1.1 <c>GET /api2/json/backup?…</c> carrying
/// <c>Connection: upgrade</c> / <c>Upgrade: proxmox-backup-protocol-v1</c>; the server replies
/// <c>101 Switching Protocols</c> and the raw connection then speaks HTTP/2 with prior knowledge.
/// We perform that handshake inside <see cref="SocketsHttpHandler.ConnectCallback"/> and let the
/// runtime drive HTTP/2 framing/HPACK/flow-control over the upgraded <see cref="SslStream"/>.
///
/// Endpoints reached over the connection (all relative to root):
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
    private readonly PbsConnectionOptions _options;
    private readonly List<ManifestFile> _files = new();

    public string BackupType { get; }
    public string BackupId { get; }
    public long BackupTime { get; }

    private BackupSession(HttpClient http, PbsConnectionOptions options, string type, string id, long time)
    {
        _http = http;
        _options = options;
        BackupType = type;
        BackupId = id;
        BackupTime = time;
    }

    /// <summary>Open a backup session, performing the protocol upgrade against the server.</summary>
    public static async Task<BackupSession> OpenAsync(
        PbsConnectionOptions options, string backupType, string backupId, long backupTimeUnix,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = options.BaseUrl.Host;
        var port = options.BaseUrl.Port;

        var query = new StringBuilder();
        query.Append("/api2/json/backup?backup-type=").Append(Uri.EscapeDataString(backupType))
             .Append("&backup-id=").Append(Uri.EscapeDataString(backupId))
             .Append("&backup-time=").Append(backupTimeUnix)
             .Append("&store=").Append(Uri.EscapeDataString(options.Datastore ?? throw new InvalidOperationException("datastore required")));
        if (!string.IsNullOrEmpty(options.Namespace))
            query.Append("&ns=").Append(Uri.EscapeDataString(options.Namespace));

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1, // one upgraded connection carries the whole session
            ConnectCallback = (ctx, cancel) =>
                UpgradeConnectAsync(host, port, query.ToString(), options, cancel),
        };

        var http = new HttpClient(handler)
        {
            // 'http' scheme so the handler uses cleartext HTTP/2 prior knowledge and does NOT add
            // its own TLS — the ConnectCallback already established TLS on the upgraded stream.
            BaseAddress = new Uri($"http://{host}:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            // Exact (not OrHigher): OrHigher makes .NET attempt the h2c Upgrade negotiation, which
            // collides with our already-upgraded stream. Exact forces HTTP/2 prior knowledge.
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var session = new BackupSession(http, options, backupType, backupId, backupTimeUnix);
        // Force the connection (and thus the upgrade) to happen now so failures surface here.
        await session.EnsureConnectedAsync(ct);
        return session;
    }

    private static async ValueTask<Stream> UpgradeConnectAsync(
        string host, int port, string path, PbsConnectionOptions options, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(host, port, ct);
        var network = new NetworkStream(socket, ownsSocket: true);

        var ssl = new SslStream(network, leaveInnerStreamOpen: false, ValidationCallback(options.Fingerprint));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            // The upgrade request itself is HTTP/1.1; don't let ALPN negotiate h2 directly.
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
        }, ct);

        var request =
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {host}:{port}\r\n" +
            $"Authorization: PBSAPIToken {options.Token.TokenId}:{options.Token.Secret}\r\n" +
            "Connection: upgrade\r\n" +
            $"Upgrade: {ProtocolId}\r\n" +
            "\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        await ssl.FlushAsync(ct);

        var statusLine = await ReadLineAsync(ssl, ct);
        // Read remaining response headers up to the blank line, leaving the h2 stream untouched.
        var headers = new List<string>();
        string? line;
        while ((line = await ReadLineAsync(ssl, ct)) is { Length: > 0 }) headers.Add(line);

        if (statusLine is null || !statusLine.Contains("101"))
        {
            var body = await ReadErrorBodyAsync(ssl, headers, ct);
            throw new IOException($"expected '101 Switching Protocols', got: {statusLine ?? "<eof>"}. Body: {body}");
        }

        // Hand back a non-SslStream wrapper: SocketsHttpHandler otherwise reads the SslStream's
        // negotiated ALPN (http/1.1, forced above for the upgrade) and refuses HTTP/2. Hiding the
        // SslStream makes the handler speak HTTP/2 with prior knowledge over the upgraded stream.
        return new PassThroughStream(ssl);
    }

    private static async Task<string> ReadErrorBodyAsync(Stream stream, List<string> headers, CancellationToken ct)
    {
        var clHeader = headers.FirstOrDefault(h => h.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase));
        if (clHeader is null || !int.TryParse(clHeader.Split(':', 2)[1].Trim(), out int len) || len <= 0 || len > 64 * 1024)
            return "<no readable body>";
        var buffer = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, len - read), ct);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static RemoteCertificateValidationCallback ValidationCallback(CertificateFingerprint? pinned) =>
        (_, cert, _, errors) =>
            pinned is null ? errors == SslPolicyErrors.None
                           : cert is not null && pinned.Matches(cert);

    // Read a single CRLF-terminated line, one byte at a time so we never over-read into the
    // HTTP/2 frames that follow the 101 response on the same stream.
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == (byte)'\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)one[0]);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        // A cheap request over the upgraded connection also validates the handshake succeeded.
        // Drain the body fully so the HTTP/2 connection is returned to the pool clean and gets
        // reused by the subsequent session requests (otherwise each request re-opens the upgrade).
        using var resp = await _http.GetAsync("previous_backup_time", HttpCompletionOption.ResponseContentRead, ct);
        await resp.Content.ReadAsByteArrayAsync(ct);
        // 200 (has previous) or 400 (none) are both fine — we only care that h2 is live.
        if ((int)resp.StatusCode >= 500)
            resp.EnsureSuccessStatusCode();
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

    /// <summary>Close a dynamic index and record its file in the manifest.</summary>
    public async Task CloseDynamicIndexAsync(string archiveName, long wid, long chunkCount, long size, ChunkDigest indexCsum, CancellationToken ct = default)
    {
        await PostJsonAsync("dynamic_close", new Dictionary<string, object>
        {
            ["wid"] = wid,
            ["chunk-count"] = chunkCount,
            ["size"] = size,
            ["csum"] = indexCsum.ToHex(),
        }, ct);
        _files.Add(new ManifestFile(archiveName, "none", size, indexCsum.ToHex()));
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
            ["unprotected"] = new Dictionary<string, object?>(),
        };
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

    /// <summary>
    /// Transparent forwarder around the upgraded <see cref="SslStream"/>. Its only purpose is to
    /// not be an <see cref="SslStream"/>, so <see cref="SocketsHttpHandler"/> uses HTTP/2 with prior
    /// knowledge instead of inspecting ALPN (which was negotiated as http/1.1 for the upgrade).
    /// </summary>
    private sealed class PassThroughStream : Stream
    {
        private readonly Stream _inner;
        public PassThroughStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); }
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
