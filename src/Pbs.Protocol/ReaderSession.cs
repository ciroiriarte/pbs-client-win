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
    private readonly ChunkCache _chunkCache = new();
    private readonly Dictionary<string, ParsedArchiveIndex> _parsedIndexes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _parsedIndexesLock = new(1, 1);

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
            warmupPath: null, ct: ct);
        return new ReaderSession(http);
    }

    /// <summary>Download a raw file from the snapshot (e.g. an index <c>*.didx</c> or a <c>*.blob</c>).</summary>
    public Task<byte[]> DownloadFileAsync(string fileName, CancellationToken ct = default) =>
        GetBytesAsync($"download?file-name={Uri.EscapeDataString(fileName)}", ct);

    /// <summary>
    /// Download and decode a single chunk. With <paramref name="crypt"/> the chunk is an encrypted
    /// DataBlob and is decrypted; otherwise it is a plaintext DataBlob.
    /// </summary>
    public async Task<byte[]> DownloadChunkAsync(ChunkDigest digest, CryptConfig? crypt = null, CancellationToken ct = default)
    {
        var blob = await GetBytesAsync($"chunk?digest={digest.ToHex()}", ct);
        return crypt is null ? DataBlob.Decode(blob) : crypt.DecryptBlob(blob);
    }

    public async Task<byte[]> ReadRangeAsync(string archiveName, long offset, long length, CryptConfig? crypt = null, CancellationToken ct = default)
    {
        var index = await GetParsedArchiveIndexAsync(archiveName, ct);
        return await ChunkRangeReader.ReadAsync(
            index.Spans,
            offset,
            length,
            async (digest, token) =>
            {
                if (_chunkCache.TryGet(digest, out var cached))
                    return cached;

                var chunk = await DownloadChunkAsync(digest, crypt, token);
                VerifyChunkDigest(digest, chunk, crypt);
                _chunkCache.Add(digest, chunk);
                return chunk;
            },
            ct);
    }

    public async Task<long> GetArchiveSizeAsync(string archiveName, CryptConfig? crypt = null, CancellationToken ct = default)
    {
        _ = crypt;
        var index = await GetParsedArchiveIndexAsync(archiveName, ct);
        return index.TotalSize;
    }

    public Stream OpenArchiveStream(string archiveName, long size, CryptConfig? crypt = null) =>
        new ArchiveStream(this, archiveName, size, crypt);

    /// <summary>
    /// Restore a whole dynamic archive: download its <c>.didx</c> index, then fetch and decode each
    /// referenced chunk in order, writing the reassembled plaintext to <paramref name="output"/>.
    /// Each chunk's digest is re-verified against the index (plain SHA-256, or the keyed digest
    /// when <paramref name="crypt"/> is supplied).
    /// </summary>
    public async Task<long> RestoreDynamicArchiveAsync(string archiveName, Stream output, CryptConfig? crypt = null, CancellationToken ct = default)
    {
        var indexBytes = await DownloadFileAsync(archiveName, ct);
        var entries = DynamicIndex.Parse(indexBytes);

        long total = 0;
        var chunkCache = new Dictionary<ChunkDigest, byte[]>();
        foreach (var entry in entries)
        {
            if (!chunkCache.TryGetValue(entry.Digest, out var chunk))
            {
                chunk = await DownloadChunkAsync(entry.Digest, crypt, ct);
                var actual = crypt is null ? ChunkDigest.Compute(chunk) : ChunkDigest.FromBytes(crypt.ComputeDigest(chunk));
                if (actual != entry.Digest)
                    throw new InvalidDataException($"restored chunk digest mismatch: index {entry.Digest}, data {actual}");
                chunkCache[entry.Digest] = chunk;
            }
            await output.WriteAsync(chunk, ct);
            total += chunk.Length;
        }
        return total;
    }

    /// <summary>
    /// Restore a fixed-size (image) archive: download its <c>.fidx</c> index, fetch and decode each
    /// referenced chunk, and write the reassembled image to <paramref name="output"/>. Returns the
    /// number of bytes written (the image size recorded in the index).
    /// </summary>
    public async Task<long> RestoreFixedArchiveAsync(string archiveName, Stream output, CryptConfig? crypt = null, CancellationToken ct = default)
    {
        var indexBytes = await DownloadFileAsync(archiveName, ct);
        var (size, _, digests) = FixedIndex.Parse(indexBytes);

        long written = 0;
        var chunkCache = new Dictionary<ChunkDigest, byte[]>();
        foreach (var digest in digests)
        {
            if (!chunkCache.TryGetValue(digest, out var chunk))
            {
                chunk = await DownloadChunkAsync(digest, crypt, ct);
                var actual = crypt is null ? ChunkDigest.Compute(chunk) : ChunkDigest.FromBytes(crypt.ComputeDigest(chunk));
                if (actual != digest)
                    throw new InvalidDataException($"restored chunk digest mismatch: index {digest}, data {actual}");
                chunkCache[digest] = chunk;
            }
            await output.WriteAsync(chunk, ct);
            written += chunk.Length;
        }
        if (written != size)
            throw new InvalidDataException($"restored image size {written} != index size {size}");
        return written;
    }

    private async Task<ParsedArchiveIndex> GetParsedArchiveIndexAsync(string archiveName, CancellationToken ct)
    {
        await _parsedIndexesLock.WaitAsync(ct);
        try
        {
            if (_parsedIndexes.TryGetValue(archiveName, out var cached))
                return cached;

            var indexBytes = await DownloadFileAsync(archiveName, ct);
            var parsed = ParseArchiveIndex(indexBytes);
            _parsedIndexes[archiveName] = parsed;
            return parsed;
        }
        finally
        {
            _parsedIndexesLock.Release();
        }
    }

    private static ParsedArchiveIndex ParseArchiveIndex(ReadOnlySpan<byte> indexBytes)
    {
        if (indexBytes.Length < 8)
            throw new InvalidDataException("archive index shorter than magic");

        if (indexBytes.Slice(0, 8).SequenceEqual(FileFormats.DynamicSizedChunkIndexMagic))
        {
            var entries = DynamicIndex.Parse(indexBytes);
            var spans = new List<ChunkSpan>(entries.Count);
            long start = 0;
            foreach (var entry in entries)
            {
                spans.Add(new ChunkSpan(start, entry.EndOffset, entry.Digest));
                start = entry.EndOffset;
            }
            return new ParsedArchiveIndex(IndexType.Dynamic, start, spans);
        }

        if (indexBytes.Slice(0, 8).SequenceEqual(FileFormats.FixedSizedChunkIndexMagic))
        {
            var (size, chunkSize, digests) = FixedIndex.Parse(indexBytes);
            var spans = new List<ChunkSpan>(digests.Count);
            for (int i = 0; i < digests.Count; i++)
            {
                var start = i * chunkSize;
                var end = Math.Min(start + chunkSize, size);
                spans.Add(new ChunkSpan(start, end, digests[i]));
            }
            return new ParsedArchiveIndex(IndexType.Fixed, size, spans);
        }

        throw new InvalidDataException("unknown archive index magic");
    }

    private static void VerifyChunkDigest(ChunkDigest expected, byte[] chunk, CryptConfig? crypt)
    {
        var actual = crypt is null ? ChunkDigest.Compute(chunk) : ChunkDigest.FromBytes(crypt.ComputeDigest(chunk));
        if (actual != expected)
            throw new InvalidDataException($"restored chunk digest mismatch: index {expected}, data {actual}");
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

    private sealed record ParsedArchiveIndex(IndexType Kind, long TotalSize, IReadOnlyList<ChunkSpan> Spans);

    private sealed class ArchiveStream : Stream
    {
        private readonly ReaderSession _session;
        private readonly string _archiveName;
        private readonly CryptConfig? _crypt;
        private long _position;

        public ArchiveStream(ReaderSession session, string archiveName, long size, CryptConfig? crypt)
        {
            if (size < 0)
                throw new ArgumentOutOfRangeException(nameof(size), "size must be non-negative");

            _session = session;
            _archiveName = archiveName;
            Length = size;
            _crypt = crypt;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "position must be non-negative");
                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || _position >= Length)
                return 0;

            var count = Math.Min(buffer.Length, checked((int)Math.Min(int.MaxValue, Length - _position)));
            var bytes = await _session.ReadRangeAsync(_archiveName, _position, count, _crypt, cancellationToken);
            bytes.AsSpan().CopyTo(buffer.Span);
            _position += bytes.Length;
            return bytes.Length;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (position < 0)
                throw new IOException("attempted to seek before the beginning of the stream");
            _position = position;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
