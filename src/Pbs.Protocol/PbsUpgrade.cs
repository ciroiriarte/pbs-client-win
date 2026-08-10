using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Pbs.Protocol;

/// <summary>
/// Establishes a PBS protocol-upgraded HTTP/2 connection and returns an <see cref="HttpClient"/>
/// bound to it. Shared by the backup (<c>proxmox-backup-protocol-v1</c>) and reader
/// (<c>proxmox-backup-reader-protocol-v1</c>) sessions.
///
/// PBS starts these sessions with an HTTP/1.1 <c>GET</c> carrying <c>Connection: upgrade</c> /
/// <c>Upgrade: &lt;protocol&gt;</c>; the server replies <c>101 Switching Protocols</c> and the raw
/// connection then speaks HTTP/2 with prior knowledge. We do that handshake inside
/// <see cref="SocketsHttpHandler.ConnectCallback"/> and hand back the upgraded stream (wrapped so
/// the handler uses HTTP/2 prior knowledge and does not layer its own TLS).
/// </summary>
public static class PbsUpgrade
{
    /// <summary>
    /// Connect and upgrade. The returned client targets the same backup group
    /// (type/id/time/store/ns). Callers must issue every request with HTTP/2 (see remarks) so the
    /// single upgraded connection is reused.
    /// </summary>
    /// <remarks>
    /// Requests built as <see cref="HttpRequestMessage"/> must set <c>Version = 2.0</c> and
    /// <c>VersionPolicy = RequestVersionExact</c>; otherwise they default to HTTP/1.1 and re-trigger
    /// the upgrade on a new connection.
    /// </remarks>
    public static async Task<HttpClient> ConnectAsync(
        PbsConnectionOptions options, string apiPath, string protocolId,
        string backupType, string backupId, long backupTimeUnix, string? warmupPath,
        bool benchmark = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = options.BaseUrl.Host;
        var port = options.BaseUrl.Port;

        var query = new StringBuilder();
        query.Append(apiPath)
             .Append("?backup-type=").Append(Uri.EscapeDataString(backupType))
             .Append("&backup-id=").Append(Uri.EscapeDataString(backupId))
             .Append("&backup-time=").Append(backupTimeUnix)
             .Append("&store=").Append(Uri.EscapeDataString(options.Datastore ?? throw new InvalidOperationException("datastore required")));
        if (!string.IsNullOrEmpty(options.Namespace))
            query.Append("&ns=").Append(Uri.EscapeDataString(options.Namespace));
        if (benchmark)
            query.Append("&benchmark=true");

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1, // one upgraded connection carries the whole session
            ConnectCallback = (_, cancel) => UpgradeConnectAsync(host, port, query.ToString(), protocolId, options, cancel),
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

        // Optionally force the connection (and thus the upgrade) now so failures surface at open.
        if (warmupPath is not null)
        {
            using var resp = await http.GetAsync(warmupPath, HttpCompletionOption.ResponseContentRead, ct);
            await resp.Content.ReadAsByteArrayAsync(ct); // drain so the h2 connection returns clean to the pool
            if ((int)resp.StatusCode >= 500) resp.EnsureSuccessStatusCode();
        }
        return http;
    }

    private static async ValueTask<Stream> UpgradeConnectAsync(
        string host, int port, string path, string protocolId, PbsConnectionOptions options, CancellationToken ct)
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

        var request = new StringBuilder()
            .Append($"GET {path} HTTP/1.1\r\n")
            .Append($"Host: {host}:{port}\r\n");
        foreach (var header in options.AuthHeaderLines())
            request.Append(header);
        request
            .Append("Connection: upgrade\r\n")
            .Append($"Upgrade: {protocolId}\r\n")
            .Append("\r\n");
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);
        await ssl.FlushAsync(ct);

        var statusLine = await ReadLineAsync(ssl, ct);
        var headers = new List<string>();
        string? line;
        while ((line = await ReadLineAsync(ssl, ct)) is { Length: > 0 }) headers.Add(line);

        if (statusLine is null || !statusLine.Contains("101"))
        {
            var body = await ReadErrorBodyAsync(ssl, headers, ct);
            throw new IOException($"expected '101 Switching Protocols', got: {statusLine ?? "<eof>"}. Body: {body}");
        }

        // Hand back a non-SslStream wrapper: SocketsHttpHandler otherwise reads the SslStream's
        // negotiated ALPN (http/1.1, forced above) and refuses HTTP/2. Hiding the SslStream makes
        // the handler speak HTTP/2 with prior knowledge over the upgraded stream.
        return new PassThroughStream(ssl);
    }

    private static RemoteCertificateValidationCallback ValidationCallback(CertificateFingerprint? pinned) =>
        (_, cert, _, errors) =>
            pinned is null ? errors == SslPolicyErrors.None
                           : cert is not null && pinned.Matches(cert);

    // Read one CRLF-terminated line, one byte at a time so we never over-read into the HTTP/2
    // frames that follow the 101 response on the same stream.
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

    /// <summary>
    /// Transparent forwarder around the upgraded <see cref="SslStream"/>. Its only purpose is to
    /// not be an <see cref="SslStream"/>, so <see cref="SocketsHttpHandler"/> uses HTTP/2 with prior
    /// knowledge instead of inspecting ALPN (negotiated as http/1.1 for the upgrade).
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
