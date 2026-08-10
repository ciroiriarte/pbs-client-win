using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;

namespace Pbs.Cli;

public static class Benchmark
{
    private const int DefaultBufferSize = 4 * 1024 * 1024;
    private const long DefaultLocalBudget = 256L * 1024 * 1024;
    private const int DefaultUploadMiB = 512;
    private static int _sink;

    public readonly record struct Result(string Name, double MegabytesPerSecond, double? Ratio = null);

    public static byte[] CreateTestBuffer(int size = DefaultBufferSize)
    {
        var buffer = new byte[size];
        var random = new Random(0x504253);

        // Semi-compressible by design: mostly structured records with sparse seeded noise. This is
        // closer to real metadata/filesystem streams than pure random data and gives zstd a useful
        // ratio while staying deterministic across platforms.
        for (int i = 0; i < buffer.Length; i++)
        {
            var structured = (byte)((i * 31 + (i >> 8) + "pbs-benchmark"[i % 13]) & 0xff);
            buffer[i] = i % 16 == 0 ? (byte)random.Next(256) : structured;
        }
        return buffer;
    }

    public static Result MeasureSha256(byte[]? buffer = null, long totalBytes = DefaultLocalBudget)
    {
        buffer ??= CreateTestBuffer();
        ChunkDigest.Compute(buffer);
        return Timed("sha256", buffer.Length, totalBytes, () =>
        {
            var digest = ChunkDigest.Compute(buffer);
            _sink ^= digest.Span[0];
        });
    }

    public static Result MeasureContentDefinedChunking(byte[]? buffer = null, long totalBytes = DefaultLocalBudget)
    {
        buffer ??= CreateTestBuffer();
        var chunker = new ContentDefinedChunker();
        chunker.Split(buffer);
        return Timed("chunking", buffer.Length, totalBytes, () =>
        {
            var chunks = chunker.Split(buffer);
            _sink ^= chunks.Count;
        });
    }

    public static Result MeasureZstdCompression(byte[]? buffer = null, long totalBytes = DefaultLocalBudget)
    {
        buffer ??= CreateTestBuffer();
        var sample = DataBlob.Encode(buffer, compress: true, compressionLevel: 1);
        var result = Timed("zstd-compress", buffer.Length, totalBytes, () =>
        {
            var blob = DataBlob.Encode(buffer, compress: true, compressionLevel: 1);
            _sink ^= blob.Length;
        });
        return result with { Ratio = sample.Length / (double)buffer.Length };
    }

    public static Result MeasureZstdDecompression(byte[]? buffer = null, long totalBytes = DefaultLocalBudget)
    {
        buffer ??= CreateTestBuffer();
        var blob = DataBlob.Encode(buffer, compress: true, compressionLevel: 1);
        DataBlob.Decode(blob);
        return Timed("zstd-decompress", buffer.Length, totalBytes, () =>
        {
            var decoded = DataBlob.Decode(blob);
            _sink ^= decoded[0];
        });
    }

    public static Result MeasureAes256GcmEncrypt(byte[]? buffer = null, long totalBytes = DefaultLocalBudget)
    {
        buffer ??= CreateTestBuffer();
        var crypt = new CryptConfig(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        crypt.EncryptBlob(buffer, compress: false);
        return Timed("aes256-gcm-encrypt", buffer.Length, totalBytes, () =>
        {
            var blob = crypt.EncryptBlob(buffer, compress: false);
            _sink ^= blob.Length;
        });
    }

    public static Result MeasureAes256GcmDecryptVerify(byte[]? buffer = null, long totalBytes = DefaultLocalBudget)
    {
        buffer ??= CreateTestBuffer();
        var crypt = new CryptConfig(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var blob = crypt.EncryptBlob(buffer, compress: false);
        crypt.DecryptBlob(blob);
        return Timed("aes256-gcm-decrypt", buffer.Length, totalBytes, () =>
        {
            var decoded = crypt.DecryptBlob(blob);
            _sink ^= decoded[0];
        });
    }

    public static async Task<int> RunAsync(Dictionary<string, string> flags)
    {
        var results = RunLocal();
        Result? tls = null;
        string? cleanupNote = null;

        if (HasRepository(flags))
        {
            var sizeMiB = ParseSize(flags);
            var upload = await RunTlsUploadAsync(flags, sizeMiB);
            tls = upload.Result;
            cleanupNote = upload.CleanupNote;
        }

        if (flags.GetValueOrDefault("output-format") == "json")
            PrintJson(results, tls);
        else
            PrintTable(results, tls, cleanupNote);

        return 0;
    }

    private static IReadOnlyList<Result> RunLocal()
    {
        var buffer = CreateTestBuffer();
        return
        [
            MeasureSha256(buffer),
            MeasureContentDefinedChunking(buffer),
            MeasureZstdCompression(buffer),
            MeasureZstdDecompression(buffer),
            MeasureAes256GcmEncrypt(buffer),
            MeasureAes256GcmDecryptVerify(buffer),
        ];
    }

    private static async Task<(Result Result, string? CleanupNote)> RunTlsUploadAsync(Dictionary<string, string> flags, int sizeMiB)
    {
        var opts = ResolveConnection(flags);
        var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var size = (long)sizeMiB * 1024 * 1024;
        var chunkSize = ChunkerOptions.FixedChunkSize;
        var archiveName = "benchmark.img.fidx";
        var chunks = new List<ChunkRef>();

        // benchmark:true opens the server's throughput-test mode — uploaded chunks are measured and
        // discarded, and no snapshot is persisted, so there is nothing to clean up afterward.
        await using var session = await BackupSession.OpenAsync(opts, "host", "benchmark", backupTime, benchmark: true);
        var wid = await session.CreateFixedIndexAsync(archiveName, size);
        var buffer = new byte[chunkSize];
        var sw = Stopwatch.StartNew();

        for (long offset = 0; offset < size; offset += chunkSize)
        {
            var want = (int)Math.Min(chunkSize, size - offset);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, want));
            var digest = ChunkDigest.Compute(buffer.AsSpan(0, want));
            var chunk = new ChunkRef(offset, want, digest);
            chunks.Add(chunk);
            var blob = DataBlob.Encode(buffer.AsSpan(0, want), compress: false);
            await session.UploadFixedChunkAsync(wid, chunk, blob);
        }

        sw.Stop();

        // Finalize best-effort: in benchmark mode the server discards the data, so index close/finish
        // may be a no-op or rejected; that does not affect the measured upload throughput.
        string? cleanupNote = null;
        try
        {
            await session.AppendFixedIndexAsync(wid, chunks);
            await session.CloseFixedIndexAsync(archiveName, wid, chunks.Count, size, FixedIndex.ComputeCsum(chunks.Select(c => c.Digest).ToList()));
            await session.FinishAsync();
        }
        catch
        {
            // ignored — benchmark upload measured above; nothing is persisted to clean up
        }

        return (new Result("tls-upload", MegabytesPerSecond(size, sw.Elapsed)), cleanupNote);
    }

    private static Result Timed(string name, int bytesPerLoop, long totalBytes, Action action)
    {
        action();
        var loops = Math.Max(1, (int)((totalBytes + bytesPerLoop - 1) / bytesPerLoop));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < loops; i++) action();
        sw.Stop();
        return new Result(name, MegabytesPerSecond((long)loops * bytesPerLoop, sw.Elapsed));
    }

    private static double MegabytesPerSecond(long bytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.000001);
        return bytes / 1024.0 / 1024.0 / seconds;
    }

    private static bool HasRepository(Dictionary<string, string> flags) =>
        flags.ContainsKey("repository") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PBS_REPOSITORY"));

    private static int ParseSize(Dictionary<string, string> flags)
    {
        var value = flags.GetValueOrDefault("size");
        if (value is null) return DefaultUploadMiB;
        if (!int.TryParse(value, out var size) || size <= 0)
            throw new ArgumentException("--size must be a positive MiB value");
        return size;
    }

    private static PbsConnectionOptions ResolveConnection(Dictionary<string, string> flags)
    {
        var repoSpec = flags.GetValueOrDefault("repository") ?? Environment.GetEnvironmentVariable("PBS_REPOSITORY")
            ?? throw new ArgumentException("no repository (use --repository or PBS_REPOSITORY)");
        var repo = PbsRepository.Parse(repoSpec);
        if (repo.AuthId is null)
            throw new ArgumentException("repository must include an API token id (user@realm!token@host:datastore)");

        var secret = flags.GetValueOrDefault("password") ?? Environment.GetEnvironmentVariable("PBS_PASSWORD")
            ?? throw new ArgumentException("no token secret (use --password or PBS_PASSWORD)");
        var fp = flags.GetValueOrDefault("fingerprint") ?? Environment.GetEnvironmentVariable("PBS_FINGERPRINT");
        var ns = flags.GetValueOrDefault("ns") ?? Environment.GetEnvironmentVariable("PBS_NAMESPACE");

        return new PbsConnectionOptions
        {
            BaseUrl = repo.BaseUrl,
            Token = new PbsApiToken(repo.AuthId, secret),
            Fingerprint = string.IsNullOrEmpty(fp) ? null : CertificateFingerprint.Parse(fp),
            Datastore = repo.Datastore,
            Namespace = ns,
        };
    }

    private static void PrintTable(IReadOnlyList<Result> results, Result? tls, string? cleanupNote)
    {
        foreach (var r in results) PrintRow(r);
        if (tls is { } t) PrintRow(t);
        if (cleanupNote is not null) Console.Error.WriteLine(cleanupNote);
    }

    private static void PrintRow(Result result)
    {
        var ratio = result.Ratio is { } r ? $"  ratio {r:0.000}" : string.Empty;
        Console.WriteLine($"{result.Name,-20} {result.MegabytesPerSecond,10:0.00} MB/s{ratio}");
    }

    private static void PrintJson(IReadOnlyList<Result> results, Result? tls)
    {
        var root = new Dictionary<string, object?>();
        foreach (var r in results) root[r.Name] = JsonResult(r);
        if (tls is { } t) root["tls"] = JsonResult(t);
        Console.WriteLine(JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, object?> JsonResult(Result result)
    {
        var item = new Dictionary<string, object?> { ["mbps"] = result.MegabytesPerSecond };
        if (result.Ratio is { } ratio) item["ratio"] = ratio;
        return item;
    }
}
