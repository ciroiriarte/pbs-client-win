using System.IO.Pipelines;
using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;

namespace Pbs.Cli;

/// <summary>Orchestration that ties the chunker, DataBlob framing, and a session together.</summary>
public static class DynamicArchive
{
    /// <summary>
    /// Bridge a push-style producer (writes to a <see cref="Stream"/>) to a pull-style consumer (reads
    /// a <see cref="Stream"/>) through a <see cref="Pipe"/> — used to stream the pxar encoder straight
    /// into <see cref="UploadStreamAsync"/> without buffering the whole archive (#4). Critically, a
    /// producer error is propagated to the consumer as a faulted read (not a clean EOF), so a partial
    /// archive can never be committed as a successful backup: the writer stream is opened with
    /// <c>leaveOpen: true</c> so only the explicit <c>CompleteAsync</c>/<c>CompleteAsync(ex)</c> — not
    /// the <c>using</c> dispose during stack unwind — decides how the pipe finishes.
    /// </summary>
    public static async Task<T> BridgeAsync<T>(Func<Stream, Task> produce, Func<Stream, Task<T>> consume)
    {
        var pipe = new Pipe();
        var producer = Task.Run(async () =>
        {
            try
            {
                await using var writer = pipe.Writer.AsStream(leaveOpen: true);
                await produce(writer);
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex);
            }
        });

        try
        {
            await using var reader = pipe.Reader.AsStream();
            return await consume(reader);
        }
        finally
        {
            await producer; // producer catches internally, so this never faults or deadlocks
        }
    }

    /// <summary>Result of an archive upload.</summary>
    /// <param name="TotalChunks">Chunks referenced by the index.</param>
    /// <param name="UploadedChunks">Unique chunks whose data was uploaded this run.</param>
    /// <param name="ReusedChunks">Chunk references satisfied by a previous snapshot (not uploaded).</param>
    /// <param name="TotalBytes">Total archive size (sum of chunk lengths).</param>
    public readonly record struct UploadStats(int TotalChunks, int UploadedChunks, int ReusedChunks, long TotalBytes);

    /// <summary>
    /// Chunk the whole <paramref name="data"/> buffer and upload it. Convenience wrapper over
    /// <see cref="UploadStreamAsync"/> for callers that already hold the bytes (e.g. the small catalog
    /// blob); the pxar path streams instead so it never buffers the whole archive (#4).
    /// </summary>
    public static Task<UploadStats> UploadAsync(
        BackupSession session, string storedName, byte[] data,
        ChunkerOptions? options = null, CryptConfig? crypt = null, bool incremental = false, CancellationToken ct = default)
        => UploadStreamAsync(session, storedName, new MemoryStream(data, writable: false), options, crypt, incremental, ct);

    /// <summary>
    /// Stream <paramref name="content"/> through the content-defined chunker, upload each unique chunk
    /// (dedup), register the index, and close it under <paramref name="storedName"/> (e.g.
    /// <c>root.pxar.didx</c>) — without ever holding the whole archive in memory (#4). When
    /// <paramref name="crypt"/> is supplied, chunks are AES-256-GCM encrypted and indexed by their keyed
    /// digest (matching the official client's <c>--keyfile</c>). When <paramref name="incremental"/> is
    /// set, chunks already present in the previous snapshot's index are referenced without re-uploading.
    /// </summary>
    public static async Task<UploadStats> UploadStreamAsync(
        BackupSession session, string storedName, Stream content,
        ChunkerOptions? options = null, CryptConfig? crypt = null, bool incremental = false, CancellationToken ct = default)
    {
        // Known chunks from the previous snapshot (also registers them server-side for reference).
        var known = new HashSet<ChunkDigest>();
        if (incremental)
        {
            var previous = await session.DownloadPreviousIndexAsync(storedName, ct);
            if (previous is not null)
                foreach (var e in DynamicIndex.Parse(previous)) known.Add(e.Digest);
        }

        var wid = await session.CreateDynamicIndexAsync(storedName, ct);

        var chunks = new List<ChunkRef>();
        var seen = new HashSet<ChunkDigest>();
        int uploaded = 0, reused = 0;
        long offset = 0;

        await foreach (var slice in new ContentDefinedChunker(options).SplitStreamAsync(content, ct))
        {
            // In encrypted mode the dedup/index digest is SHA256(plaintext ‖ id_key), not the plain
            // chunk SHA-256; use the keyed digest everywhere downstream.
            var digest = crypt is null
                ? ChunkDigest.Compute(slice)
                : ChunkDigest.FromBytes(crypt.ComputeDigest(slice));
            var chunk = new ChunkRef(offset, slice.Length, digest);
            offset += slice.Length;
            chunks.Add(chunk);

            if (known.Contains(digest)) { reused++; continue; }  // server already has it
            if (!seen.Add(digest)) continue;                     // already uploaded this run
            var blob = crypt is null ? DataBlob.Encode(slice, compress: true) : crypt.EncryptBlob(slice, compress: true);
            await session.UploadDynamicChunkAsync(wid, chunk, blob, ct);
            uploaded++;
        }

        await session.AppendDynamicIndexAsync(wid, chunks, ct);
        await session.CloseDynamicIndexAsync(storedName, wid, chunks.Count, offset,
            DynamicIndex.ComputeCsum(chunks), crypt is null ? "none" : "encrypt", ct);

        return new UploadStats(chunks.Count, uploaded, reused, offset);
    }
}
