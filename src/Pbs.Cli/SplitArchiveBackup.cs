using System.Text.Json;
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
        if (mode == ChangeDetectionMode.Metadata && !incremental)
            throw new ArgumentException("--change-detection-mode metadata reuses the previous snapshot; it cannot be combined with --no-incremental");

        // The previous payload index: known chunks for dedup and, in metadata mode, what gets injected.
        // Downloading it also registers its chunks with the server for this session. It is used only
        // after the previous manifest checks out (signature when encrypted, crypt modes, index csum).
        IReadOnlyList<DynamicIndexEntry> previousEntries = Array.Empty<DynamicIndexEntry>();
        JsonDocument? manifest = null;
        Reference? reference = null;
        try
        {
            var previousPayload = incremental ? await session.DownloadPreviousIndexAsync(payloadName!, ct) : null;
            if (previousPayload is not null)
            {
                manifest = await session.DownloadPreviousManifestAsync(ct);
                var problem = manifest is null ? "previous manifest is missing" : ManifestVerifier.CheckReusable(manifest.RootElement, crypt);
                if (problem is null)
                {
                    var entries = DynamicIndex.Parse(previousPayload);
                    try
                    {
                        ManifestVerifier.VerifyDynamicIndex(manifest!.RootElement, payloadName!, entries);
                        previousEntries = entries;
                    }
                    catch (InvalidDataException ex) { problem = ex.Message; }
                }
                if (problem is not null)
                    log($"  not reusing the previous snapshot: {problem}");
            }

            if (mode == ChangeDetectionMode.Metadata && previousEntries.Count > 0)
                reference = await LoadReferenceAsync(session, opts, manifest!.RootElement, metaName, crypt, previousEntries, log, ct);
        }
        finally
        {
            manifest?.Dispose();
        }

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
        BackupSession session, PbsConnectionOptions opts, JsonElement manifest, string metaName, CryptConfig? crypt,
        IReadOnlyList<DynamicIndexEntry> previousEntries, Action<string> log, CancellationToken ct)
    {
        var previousTime = await session.GetPreviousBackupTimeAsync(ct);
        if (previousTime is null)
            return null;
        // The manifest must describe the snapshot the reference reader opens.
        if (!manifest.TryGetProperty("backup-time", out var bt) || bt.GetInt64() != previousTime.Value)
        {
            log("  previous manifest does not match the previous snapshot: encoding all files");
            return null;
        }

        await using var reader = await ReaderSession.OpenAsync(opts, session.BackupType, session.BackupId, previousTime.Value, ct);
        IReadOnlyList<DynamicIndexEntry> metaEntries;
        try
        {
            metaEntries = DynamicIndex.Parse(await reader.DownloadFileAsync(metaName, ct));
            ManifestVerifier.VerifyDynamicIndex(manifest, metaName, metaEntries);
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException)
        {
            log($"  previous snapshot has no usable split archive '{metaName}' ({ex.Message}): encoding all files");
            return null;
        }

        using var metaBuffer = new MemoryStream();
        await reader.RestoreDynamicArchiveAsync(metaEntries, metaBuffer, crypt, ct);
        metaBuffer.Position = 0;
        var index = (await PxarIndexer.BuildIndexAsync(metaBuffer, ct))
            .Where(e => e.Kind == PxarEntryKind.File)
            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var chunks = previousEntries.Select(e => new PreviousPayloadChunk(e.EndOffset, e.Digest.Span.ToArray())).ToList();

        log($"  using previous snapshot {previousTime} as metadata reference for '{metaName}'");
        return new Reference(previousTime.Value, index, chunks);
    }
}
