using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;

namespace Pbs.Cli;

/// <summary>
/// Orchestrates a fixed-size (image) backup: read the source in fixed <c>chunkSize</c> blocks,
/// upload each unique block, and register a FIDX index. Streams the source, so it works for both a
/// synthetic <see cref="MemoryStream"/> and a raw volume device of arbitrary size.
/// </summary>
public static class FixedImage
{
    /// <summary>
    /// Back up <paramref name="source"/> (exactly <paramref name="size"/> bytes) as a fixed index
    /// under <paramref name="storedName"/> (e.g. <c>disk0.img.fidx</c>). With <paramref name="crypt"/>
    /// chunks are encrypted and keyed-digested; with <paramref name="incremental"/> unchanged blocks
    /// from the previous snapshot are reused without re-upload.
    /// </summary>
    public static async Task<DynamicArchive.UploadStats> UploadAsync(
        BackupSession session, string storedName, Stream source, long size,
        int chunkSize = ChunkerOptions.FixedChunkSize, CryptConfig? crypt = null, bool incremental = false,
        CancellationToken ct = default)
    {
        var known = new HashSet<ChunkDigest>();
        if (incremental)
        {
            var previous = await session.DownloadPreviousIndexAsync(storedName, ct);
            if (previous is not null)
                foreach (var d in FixedIndex.Parse(previous).Digests) known.Add(d);
        }

        var wid = await session.CreateFixedIndexAsync(storedName, size, ct);

        var chunks = new List<ChunkRef>();
        var seen = new HashSet<ChunkDigest>();
        var buffer = new byte[chunkSize];
        int uploaded = 0, reused = 0;
        long offset = 0;

        while (offset < size)
        {
            int want = (int)Math.Min(chunkSize, size - offset);
            await ReadExactAsync(source, buffer, want, ct);
            var slice = buffer.AsSpan(0, want).ToArray();

            var digest = crypt is null
                ? ChunkDigest.Compute(slice)
                : ChunkDigest.FromBytes(crypt.ComputeDigest(slice));
            var chunkRef = new ChunkRef(offset, want, digest);
            chunks.Add(chunkRef);

            if (known.Contains(digest)) { reused++; }
            else if (seen.Add(digest))
            {
                var blob = crypt is null ? DataBlob.Encode(slice, compress: true) : crypt.EncryptBlob(slice, compress: true);
                await session.UploadFixedChunkAsync(wid, chunkRef, blob, ct);
                uploaded++;
            }

            offset += want;
        }

        await session.AppendFixedIndexAsync(wid, chunks, ct);
        var digests = chunks.Select(c => c.Digest).ToList();
        await session.CloseFixedIndexAsync(storedName, wid, chunks.Count, size, FixedIndex.ComputeCsum(digests),
            crypt is null ? "none" : "encrypt", ct);

        return new DynamicArchive.UploadStats(chunks.Count, uploaded, reused);
    }

    private static async Task ReadExactAsync(Stream source, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await source.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException("image source ended before the declared size");
            read += n;
        }
    }
}
