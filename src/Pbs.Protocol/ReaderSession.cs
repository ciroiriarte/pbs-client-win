using System.Net;
using Pbs.Abstractions;

namespace Pbs.Protocol;

/// <summary>
/// A PBS reader (restore) session over the upgraded <c>proxmox-backup-reader-protocol-v1</c>
/// connection (see <see cref="PbsUpgrade"/>). Endpoints (all GET, raw octet-stream bodies):
/// <list type="bullet">
/// <item><c>GET download?file-name=&lt;name&gt;</c> → raw file bytes (an index or a blob)</item>
/// <item><c>GET chunk?digest=&lt;hex&gt;</c> → the DataBlob for that chunk</item>
/// </list>
/// </summary>
public sealed class ReaderSession : IAsyncDisposable
{
    public const string ProtocolId = "proxmox-backup-reader-protocol-v1";

    private readonly HttpClient _http;

    private ReaderSession(HttpClient http) => _http = http;

    /// <summary>Open a reader session for a specific snapshot, performing the protocol upgrade.</summary>
    public static async Task<ReaderSession> OpenAsync(
        PbsConnectionOptions options, string backupType, string backupId, long backupTimeUnix,
        CancellationToken ct = default)
    {
        // 'download'/'chunk' need params and 'speedtest' returns 1 MiB, so skip the warm-up and let
        // the first real download surface any upgrade error.
        var http = await PbsUpgrade.ConnectAsync(
            options, "/api2/json/reader", ProtocolId, backupType, backupId, backupTimeUnix,
            warmupPath: null, ct);
        return new ReaderSession(http);
    }

    /// <summary>Download a raw file from the snapshot (e.g. an index <c>*.didx</c> or a <c>*.blob</c>).</summary>
    public Task<byte[]> DownloadFileAsync(string fileName, CancellationToken ct = default) =>
        GetBytesAsync($"download?file-name={Uri.EscapeDataString(fileName)}", ct);

    /// <summary>Download and decode a single chunk (server returns its DataBlob).</summary>
    public async Task<byte[]> DownloadChunkAsync(ChunkDigest digest, CancellationToken ct = default)
    {
        var blob = await GetBytesAsync($"chunk?digest={digest.ToHex()}", ct);
        return DataBlob.Decode(blob);
    }

    /// <summary>
    /// Restore a whole dynamic archive: download its <c>.didx</c> index, then fetch and decode each
    /// referenced chunk in order, writing the reassembled plaintext to <paramref name="output"/>.
    /// Verifies each chunk's SHA-256 against the index digest.
    /// </summary>
    public async Task<long> RestoreDynamicArchiveAsync(string archiveName, Stream output, CancellationToken ct = default)
    {
        var indexBytes = await DownloadFileAsync(archiveName, ct);
        var entries = DynamicIndex.Parse(indexBytes);

        long total = 0;
        var chunkCache = new Dictionary<ChunkDigest, byte[]>();
        foreach (var entry in entries)
        {
            if (!chunkCache.TryGetValue(entry.Digest, out var chunk))
            {
                chunk = await DownloadChunkAsync(entry.Digest, ct);
                var actual = ChunkDigest.Compute(chunk);
                if (actual != entry.Digest)
                    throw new InvalidDataException($"restored chunk digest mismatch: index {entry.Digest}, data {actual}");
                chunkCache[entry.Digest] = chunk;
            }
            await output.WriteAsync(chunk, ct);
            total += chunk.Length;
        }
        return total;
    }

    private async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact, // reuse the single upgraded h2 connection
        };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"GET {path} -> {(int)resp.StatusCode}: {text}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
