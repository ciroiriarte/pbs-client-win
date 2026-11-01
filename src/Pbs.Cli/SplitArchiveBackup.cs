using Pbs.Abstractions;
using Pbs.Protocol;
using Pbs.Pxar;

namespace Pbs.Cli;

/// <summary>Upstream's <c>--change-detection-mode</c> for file backups (issue #33).</summary>
public enum ChangeDetectionMode
{
    /// <summary>One combined archive (<c>name.pxar.didx</c>) plus a catalog. The default.</summary>
    Legacy,
    /// <summary>Split archive (<c>name.mpxar.didx</c> + <c>name.ppxar.didx</c>); every file is read.</summary>
    Data,
    /// <summary>Split archive; files unchanged since the previous snapshot reuse its payload chunks unread.</summary>
    Metadata,
}

/// <summary>Split-archive (pxar v2) backup of one directory tree: plan, encode, upload.</summary>
public static class SplitArchiveBackup
{
    public static ChangeDetectionMode ParseMode(string? value) => (value ?? "legacy").ToLowerInvariant() switch
    {
        "legacy" => ChangeDetectionMode.Legacy,
        "data" => ChangeDetectionMode.Data,
        "metadata" => ChangeDetectionMode.Metadata,
        _ => throw new ArgumentException($"--change-detection-mode must be legacy, data or metadata (got '{value}')"),
    };

    /// <summary>The previous snapshot's split archive, as a reuse reference.</summary>
    public sealed record Reference(
        long BackupTime,
        IReadOnlyDictionary<string, PxarEntryLocation> Index,
        IReadOnlyList<PreviousPayloadChunk> Chunks);

    public readonly record struct Result(
        DynamicArchive.UploadStats Metadata, DynamicArchive.UploadStats Payload, int Injected, PayloadPlanStats Plan, bool UsedReference);

    public static async Task<Result> BackupAsync(
        BackupSession session, PbsConnectionOptions opts, string archiveBase, PxarDirNode tree,
        ChangeDetectionMode mode, CryptConfig? crypt, bool incremental, Action<string> log, CancellationToken ct = default)
    {
        var (metaName, payloadName) = PxarArchiveNames.SplitPair(archiveBase);

        // The previous payload index: known chunks for dedup and, in metadata mode, what gets injected.
        // Downloading it also registers its chunks with the server for this session.
        var previousPayload = incremental || mode == ChangeDetectionMode.Metadata
            ? await session.DownloadPreviousIndexAsync(payloadName!, ct)
            : null;
        var previousEntries = previousPayload is null ? Array.Empty<DynamicIndexEntry>() : DynamicIndex.Parse(previousPayload);

        Reference? reference = null;
        if (mode == ChangeDetectionMode.Metadata && previousEntries.Count > 0)
            reference = await LoadReferenceAsync(session, opts, metaName, payloadName!, previousEntries, crypt, log, ct);

        var plan = PxarPayloadPlanner.Plan(PxarEncoder.FilesInEncodeOrder(tree), reference?.Index, reference?.Chunks);

        var metaStats = await DynamicArchive.BridgeAsync(
            writer => PxarEncoder.EncodeMetadataAsync(tree, writer, plan, ct),
            reader => DynamicArchive.UploadStreamAsync(session, metaName, reader, crypt: crypt, incremental: incremental, ct: ct));

        var uploader = await PayloadUploader.StartAsync(session, payloadName!, previousEntries.Select(e => e.Digest),
            plan.EncodedFileOffsets, crypt, ct: ct);
        await PxarPayloadWriter.WriteAsync(plan, uploader, ct);
        var (payloadStats, injected) = await uploader.FinishAsync(ct);

        return new Result(metaStats, payloadStats, injected, plan.Stats, reference is not null);
    }

    private static async Task<Reference?> LoadReferenceAsync(
        BackupSession session, PbsConnectionOptions opts, string metaName, string payloadName,
        IReadOnlyList<DynamicIndexEntry> previousEntries, CryptConfig? crypt, Action<string> log, CancellationToken ct)
    {
        var previousTime = await session.GetPreviousBackupTimeAsync(ct);
        using var manifest = await session.DownloadPreviousManifestAsync(ct);
        if (previousTime is null || manifest is null)
            return null;

        var files = manifest.RootElement.TryGetProperty("files", out var f) ? f.EnumerateArray().ToList() : new();
        var meta = files.FirstOrDefault(x => x.GetProperty("filename").GetString() == metaName);
        if (meta.ValueKind == System.Text.Json.JsonValueKind.Undefined ||
            !files.Any(x => x.GetProperty("filename").GetString() == payloadName))
        {
            log($"  previous snapshot has no split archive '{metaName}': encoding all files");
            return null;
        }

        // Injected chunks are referenced by digest as-is, so they must be in this backup's crypt mode
        // and, when encrypted, under this key (encrypted digests are keyed).
        var previousMode = meta.TryGetProperty("crypt-mode", out var cm) ? cm.GetString() : "none";
        var currentMode = crypt is null ? "none" : "encrypt";
        if (previousMode != currentMode)
        {
            log($"  previous snapshot crypt-mode '{previousMode}' differs from '{currentMode}': encoding all files");
            return null;
        }
        if (crypt is not null)
        {
            var previousKey = manifest.RootElement.TryGetProperty("unprotected", out var u) && u.TryGetProperty("key-fingerprint", out var k)
                ? k.GetString() : null;
            if (!string.Equals(previousKey, crypt.FingerprintHex(), StringComparison.OrdinalIgnoreCase))
            {
                log("  previous snapshot was encrypted with a different key: encoding all files");
                return null;
            }
        }

        await using var reader = await ReaderSession.OpenAsync(opts, session.BackupType, session.BackupId, previousTime.Value, ct);
        using var metaBuffer = new MemoryStream();
        await reader.RestoreDynamicArchiveAsync(metaName, metaBuffer, crypt, ct);
        metaBuffer.Position = 0;
        var index = (await PxarIndexer.BuildIndexAsync(metaBuffer, ct))
            .Where(e => e.Kind == PxarEntryKind.File)
            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var chunks = previousEntries.Select(e => new PreviousPayloadChunk(e.EndOffset, e.Digest.Span.ToArray())).ToList();

        log($"  using previous snapshot {previousTime} as metadata reference for '{metaName}'");
        return new Reference(previousTime.Value, index, chunks);
    }
}
