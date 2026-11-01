using System.Runtime.CompilerServices;
using Pbs.Abstractions;

namespace Pbs.Chunker;

/// <summary>
/// Content-defined chunker using a Gear rolling hash. Boundaries depend only on local byte
/// content, so unchanged regions of a source produce identical chunks across backups (the
/// property that makes incremental dedup work). Deterministic: the Gear table is derived from a
/// fixed seed, so the same input always yields the same chunks on every platform.
/// </summary>
public sealed class ContentDefinedChunker
{
    // Fixed, reproducible Gear substitution table (256 entries). Built from splitmix64 so the
    // chunker is deterministic and platform-independent without shipping a 2 KiB literal table.
    private static readonly ulong[] Gear = BuildGearTable(0x9E3779B97F4A7C15UL);

    private readonly ChunkerOptions _options;
    private readonly ulong _mask;

    public ContentDefinedChunker(ChunkerOptions? options = null)
    {
        _options = options ?? new ChunkerOptions();
        _options.Validate();
        _mask = (1UL << _options.MaskBits) - 1;
    }

    /// <summary>
    /// Split <paramref name="data"/> into content-defined chunks, returning a reference (offset,
    /// length, SHA-256 digest) for each. Concatenating the chunks in order reproduces the input.
    /// </summary>
    public IReadOnlyList<ChunkRef> Split(ReadOnlySpan<byte> data)
    {
        var result = new List<ChunkRef>();
        int pos = 0;
        int n = data.Length;
        while (pos < n)
        {
            int end = NextBoundary(data, pos);
            var slice = data[pos..end];
            result.Add(new ChunkRef(pos, slice.Length, ChunkDigest.Compute(slice)));
            pos = end;
        }
        return result;
    }

    /// <summary>
    /// Streaming counterpart of <see cref="Split"/>: read <paramref name="input"/> and yield each
    /// content-defined chunk's bytes without ever holding the whole input in memory. Boundaries are
    /// found with the same <see cref="NextBoundary"/> as the in-memory path — a chunk boundary depends
    /// only on the bytes up to it, so a boundary determined once MaxSize bytes are buffered is final —
    /// so for any input this yields byte-identical chunks (and therefore identical digests) to
    /// <c>Split(fullInput)</c>. Memory use is bounded to roughly MaxSize plus one read block.
    /// </summary>
    public IAsyncEnumerable<byte[]> SplitStreamAsync(Stream input, CancellationToken ct = default)
        => SplitStreamAsync(input, suggestedBoundaries: null, basePosition: 0, ct);

    /// <summary>
    /// <see cref="SplitStreamAsync(Stream, CancellationToken)"/> with suggested boundaries, like
    /// upstream's <c>PayloadChunker</c> for split-archive payload streams (issue #33): a chunk ends at
    /// the first suggested position that makes it at least MinSize (and at most MaxSize) long, unless
    /// the content-defined boundary comes first. Suggesting each file's start makes chunks align with
    /// files, so a later backup can reuse an unchanged file's chunks with little padding.
    /// </summary>
    /// <param name="suggestedBoundaries">Ascending absolute positions (in the coordinates of <paramref name="basePosition"/>).</param>
    /// <param name="basePosition">Absolute position of the first byte of <paramref name="input"/>.</param>
    public async IAsyncEnumerable<byte[]> SplitStreamAsync(
        Stream input, IReadOnlyList<long>? suggestedBoundaries, long basePosition, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        int next = 0; // first suggestion not yet behind the current chunk start
        long consumed = 0; // bytes of input already yielded

        const int ReadBlock = 128 * 1024;
        var buf = new byte[_options.MaxSize + ReadBlock];
        var len = 0;          // valid bytes in buf
        var eof = false;

        while (!eof || len > 0)
        {
            // Fill until we hold at least a full max-size chunk's worth (so the next boundary is final) or EOF.
            while (!eof && len < _options.MaxSize)
            {
                var n = await input.ReadAsync(buf.AsMemory(len, buf.Length - len), ct);
                if (n == 0) { eof = true; break; }
                len += n;
            }

            var start = 0;
            while (start < len && (len - start >= _options.MaxSize || eof))
            {
                var end = NextBoundary(buf.AsSpan(0, len), start);
                if (suggestedBoundaries is not null)
                {
                    long chunkStart = basePosition + consumed + start;
                    while (next < suggestedBoundaries.Count && suggestedBoundaries[next] - chunkStart < _options.MinSize)
                        next++; // behind us, or would make a chunk below MinSize: ignored (as upstream)
                    if (next < suggestedBoundaries.Count)
                    {
                        long cut = suggestedBoundaries[next] - chunkStart; // >= MinSize
                        if (cut < end - start) end = start + (int)cut;      // before the content-defined cut
                    }
                }
                yield return buf[start..end];
                start = end;
            }
            consumed += start;

            if (start > 0)
            {
                Array.Copy(buf, start, buf, 0, len - start);
                len -= start;
            }
        }
    }

    /// <summary>
    /// Return the exclusive end index of the chunk that starts at <paramref name="start"/>,
    /// honoring the configured minimum and maximum chunk sizes.
    /// </summary>
    internal int NextBoundary(ReadOnlySpan<byte> data, int start)
    {
        int n = data.Length;
        int limit = (int)Math.Min((long)start + _options.MaxSize, n);
        ulong hash = 0;
        int i = start;
        while (i < limit)
        {
            hash = (hash << 1) + Gear[data[i]];
            i++;
            if (i - start >= _options.MinSize && (hash & _mask) == 0)
                return i;
        }
        return i; // hit MaxSize or end-of-input
    }

    private static ulong[] BuildGearTable(ulong seed)
    {
        var table = new ulong[256];
        ulong x = seed;
        for (int i = 0; i < 256; i++)
        {
            // splitmix64
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            table[i] = z;
        }
        return table;
    }
}
