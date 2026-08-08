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
