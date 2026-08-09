using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;

namespace Pbs.Cli;

/// <summary>Orchestration that ties the chunker, DataBlob framing, and a session together.</summary>
public static class DynamicArchive
{
    /// <summary>
    /// Chunk <paramref name="data"/>, upload each unique chunk (dedup), register the index, and
    /// close it under <paramref name="storedName"/> (e.g. <c>root.pxar.didx</c>). When
    /// <paramref name="crypt"/> is supplied, chunks are AES-256-GCM encrypted and indexed by their
    /// keyed digest (matching the official client's <c>--keyfile</c>).
    /// </summary>
    public static async Task UploadAsync(
        BackupSession session, string storedName, byte[] data,
        ChunkerOptions? options = null, CryptConfig? crypt = null, CancellationToken ct = default)
    {
        var boundaries = new ContentDefinedChunker(options).Split(data);

        // In encrypted mode the dedup/index digest is SHA256(plaintext ‖ id_key), not the plain
        // chunk SHA-256, so remap each chunk's digest before it is used anywhere downstream.
        IReadOnlyList<ChunkRef> chunks = crypt is null
            ? boundaries
            : boundaries.Select(c => c with { Digest = ChunkDigest.FromBytes(crypt.ComputeDigest(data.AsSpan((int)c.Offset, c.Length))) }).ToList();

        var wid = await session.CreateDynamicIndexAsync(storedName, ct);

        var seen = new HashSet<ChunkDigest>();
        foreach (var c in chunks)
        {
            if (!seen.Add(c.Digest)) continue;
            var slice = data.AsSpan((int)c.Offset, c.Length).ToArray();
            var blob = crypt is null ? DataBlob.Encode(slice, compress: true) : crypt.EncryptBlob(slice, compress: true);
            await session.UploadDynamicChunkAsync(wid, c, blob, ct);
        }

        await session.AppendDynamicIndexAsync(wid, chunks, ct);
        await session.CloseDynamicIndexAsync(storedName, wid, chunks.Count, data.Length,
            DynamicIndex.ComputeCsum(chunks), crypt is null ? "none" : "encrypt", ct);
    }
}
