using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;

namespace Pbs.Cli;

/// <summary>Orchestration that ties the chunker, DataBlob framing, and a session together.</summary>
public static class DynamicArchive
{
    /// <summary>Result of an archive upload.</summary>
    /// <param name="TotalChunks">Chunks referenced by the index.</param>
    /// <param name="UploadedChunks">Unique chunks whose data was uploaded this run.</param>
    /// <param name="ReusedChunks">Chunk references satisfied by a previous snapshot (not uploaded).</param>
    public readonly record struct UploadStats(int TotalChunks, int UploadedChunks, int ReusedChunks);

    /// <summary>
    /// Chunk <paramref name="data"/>, upload each unique chunk (dedup), register the index, and
    /// close it under <paramref name="storedName"/> (e.g. <c>root.pxar.didx</c>). When
    /// <paramref name="crypt"/> is supplied, chunks are AES-256-GCM encrypted and indexed by their
    /// keyed digest (matching the official client's <c>--keyfile</c>). When
    /// <paramref name="incremental"/> is set, chunks already present in the previous snapshot's
    /// index are referenced without re-uploading their data.
    /// </summary>
    public static async Task<UploadStats> UploadAsync(
        BackupSession session, string storedName, byte[] data,
        ChunkerOptions? options = null, CryptConfig? crypt = null, bool incremental = false, CancellationToken ct = default)
    {
        var boundaries = new ContentDefinedChunker(options).Split(data);

        // In encrypted mode the dedup/index digest is SHA256(plaintext ‖ id_key), not the plain
        // chunk SHA-256, so remap each chunk's digest before it is used anywhere downstream.
        IReadOnlyList<ChunkRef> chunks = crypt is null
            ? boundaries
            : boundaries.Select(c => c with { Digest = ChunkDigest.FromBytes(crypt.ComputeDigest(data.AsSpan((int)c.Offset, c.Length))) }).ToList();

        // Known chunks from the previous snapshot (also registers them server-side for reference).
        var known = new HashSet<ChunkDigest>();
        if (incremental)
        {
            var previous = await session.DownloadPreviousIndexAsync(storedName, ct);
            if (previous is not null)
                foreach (var e in DynamicIndex.Parse(previous)) known.Add(e.Digest);
        }

        var wid = await session.CreateDynamicIndexAsync(storedName, ct);

        int uploaded = 0, reused = 0;
        var seen = new HashSet<ChunkDigest>();
        foreach (var c in chunks)
        {
            if (known.Contains(c.Digest)) { reused++; continue; }  // server already has it
            if (!seen.Add(c.Digest)) continue;                     // already uploaded this run
            var slice = data.AsSpan((int)c.Offset, c.Length).ToArray();
            var blob = crypt is null ? DataBlob.Encode(slice, compress: true) : crypt.EncryptBlob(slice, compress: true);
            await session.UploadDynamicChunkAsync(wid, c, blob, ct);
            uploaded++;
        }

        await session.AppendDynamicIndexAsync(wid, chunks, ct);
        await session.CloseDynamicIndexAsync(storedName, wid, chunks.Count, data.Length,
            DynamicIndex.ComputeCsum(chunks), crypt is null ? "none" : "encrypt", ct);

        return new UploadStats(chunks.Count, uploaded, reused);
    }
}
