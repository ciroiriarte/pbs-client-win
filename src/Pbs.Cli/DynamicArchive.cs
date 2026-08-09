using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;

namespace Pbs.Cli;

/// <summary>Orchestration that ties the chunker, DataBlob framing, and a session together.</summary>
public static class DynamicArchive
{
    /// <summary>
    /// Chunk <paramref name="data"/>, upload each unique chunk (dedup), register the index, and
    /// close it under <paramref name="storedName"/> (e.g. <c>root.pxar.didx</c>).
    /// </summary>
    public static async Task UploadAsync(
        BackupSession session, string storedName, byte[] data, ChunkerOptions? options = null, CancellationToken ct = default)
    {
        var chunks = new ContentDefinedChunker(options).Split(data);
        var wid = await session.CreateDynamicIndexAsync(storedName, ct);

        var seen = new HashSet<ChunkDigest>();
        foreach (var c in chunks)
        {
            if (!seen.Add(c.Digest)) continue;
            var blob = DataBlob.Encode(data.AsSpan((int)c.Offset, c.Length).ToArray(), compress: true);
            await session.UploadDynamicChunkAsync(wid, c, blob, ct);
        }

        await session.AppendDynamicIndexAsync(wid, chunks, ct);
        await session.CloseDynamicIndexAsync(storedName, wid, chunks.Count, data.Length, DynamicIndex.ComputeCsum(chunks), ct);
    }
}
