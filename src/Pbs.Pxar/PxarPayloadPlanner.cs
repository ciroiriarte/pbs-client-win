namespace Pbs.Pxar;

/// <summary>A chunk of the previous snapshot's payload archive index: its end offset and digest.</summary>
public readonly record struct PreviousPayloadChunk(long End, byte[] Digest);

/// <summary>One step of the new payload stream, in order.</summary>
public abstract record PayloadSegment;

/// <summary>Encode this file's PAYLOAD (header + data) at the current payload position.</summary>
public sealed record EncodeFileSegment(PxarFileNode File) : PayloadSegment;

/// <summary>
/// Append these previous-snapshot chunks verbatim at the current payload position (no data is read
/// or uploaded). <see cref="Sizes"/>[i] is the length of <see cref="Chunks"/>[i].
/// </summary>
public sealed record InjectChunksSegment(IReadOnlyList<PreviousPayloadChunk> Chunks, IReadOnlyList<long> Sizes) : PayloadSegment
{
    public long TotalSize => Sizes.Sum();
}

/// <summary>Reuse statistics, mirroring what upstream reports for metadata-mode backups.</summary>
public readonly record struct PayloadPlanStats(
    int FilesReused, long BytesReused, int FilesReencoded, long BytesReencoded,
    int ChunksInjected, long PaddingBytes);

/// <summary>
/// The layout of a new split archive's payload stream (<c>.ppxar</c>): every regular file's PAYLOAD
/// header offset (what its PAYLOAD_REF in the metadata stream stores), and the ordered segments that
/// produce the stream between the PAYLOAD_START_MARKER and PAYLOAD_TAIL_MARKER.
/// </summary>
public sealed class PayloadPlan
{
    private readonly Dictionary<PxarFileNode, long> _offsets;

    internal PayloadPlan(Dictionary<PxarFileNode, long> offsets, IReadOnlyList<PayloadSegment> segments, long endPosition, PayloadPlanStats stats)
    {
        _offsets = offsets;
        Segments = segments;
        EndPosition = endPosition;
        Stats = stats;
    }

    public IReadOnlyList<PayloadSegment> Segments { get; }

    /// <summary>Payload stream position after the last segment (where the tail marker goes).</summary>
    public long EndPosition { get; }

    public PayloadPlanStats Stats { get; }

    /// <summary>Offset of <paramref name="file"/>'s PAYLOAD header in the payload stream.</summary>
    public long OffsetOf(PxarFileNode file) => _offsets[file];

    /// <summary>
    /// Payload positions where newly encoded files start, ascending — the chunker's suggested
    /// boundaries, so new chunks align with files (upstream sends the same for every encoded file).
    /// </summary>
    public IReadOnlyList<long> EncodedFileOffsets =>
        Segments.OfType<EncodeFileSegment>().Select(s => _offsets[s.File]).ToList();
}

/// <summary>
/// Decides, for a split-archive backup, which files are re-encoded and which reuse the previous
/// snapshot's payload chunks ("metadata" change detection, issue #33). The rules follow upstream
/// <c>pbs-client/src/pxar/create.rs</c>:
/// <list type="bullet">
///   <item>a file is reusable when the previous metadata archive has the same path as a split-archive
///         regular file with the same size, mode and mtime (seconds + nanoseconds), and its previous
///         payload offset is strictly greater than the last one accepted;</item>
///   <item>consecutive reusable files whose previous payloads are contiguous form a run; a gap or a
///         non-reusable file ends the run;</item>
///   <item>a run is reused by injecting the previous chunks that cover it — only if the bytes of other
///         data those chunks drag along (padding) are at most 10% of the injected size; otherwise the
///         run's files are re-encoded.</item>
/// </list>
/// Unlike upstream's streaming look-ahead cache, this plans the whole stream up front (the backup tree
/// and every file length are known before encoding), which yields the same kind of archive and keeps
/// the decision logic pure and testable. With no previous reference every file is encoded ("data"
/// mode). Files are read for exactly their planned length, so planned offsets hold.
/// </summary>
public static class PxarPayloadPlanner
{
    /// <summary>Upstream's CHUNK_PADDING_THRESHOLD.</summary>
    public const double PaddingThreshold = 0.1;

    /// <param name="files">Regular files in encode order with their archive-relative paths.</param>
    /// <param name="previousIndex">Previous metadata archive index by relative path, or null.</param>
    /// <param name="previousChunks">Previous payload archive index (sorted by end offset), or null.</param>
    public static PayloadPlan Plan(
        IReadOnlyList<(PxarFileNode File, string RelativePath)> files,
        IReadOnlyDictionary<string, PxarEntryLocation>? previousIndex = null,
        IReadOnlyList<PreviousPayloadChunk>? previousChunks = null)
    {
        var offsets = new Dictionary<PxarFileNode, long>(ReferenceEqualityComparer.Instance);
        var segments = new List<PayloadSegment>();
        long pos = PxarFormat.HeaderSize; // after PAYLOAD_START_MARKER
        long? lastAccepted = null;
        int filesReused = 0, filesReencoded = 0, chunksInjected = 0;
        long bytesReused = 0, bytesReencoded = 0, padding = 0;

        bool canReuse = previousIndex is not null && previousChunks is { Count: > 0 };
        long previousEnd = canReuse ? previousChunks![^1].End : 0;

        var run = new List<(PxarFileNode File, long PrevOffset)>();
        long runStart = 0, runEnd = 0;

        // The last chunk of the last reused run, held back after a hole (upstream's keep_last_chunk):
        // when the next run starts inside the same chunk it is shared instead of injected twice, and
        // only the dead bytes between the two runs count as padding. It sits at payload position `pos`
        // (not yet added to `pos`). UsedEnd: previous-archive offset where the last run's data ended.
        (PreviousPayloadChunk Chunk, long Size, long UsedEnd)? pending = null;

        void EmitPending()
        {
            if (pending is not { } p) return;
            segments.Add(new InjectChunksSegment(new[] { p.Chunk }, new[] { p.Size }));
            pos += p.Size;
            chunksInjected++;
            padding += p.Chunk.End - p.UsedEnd; // its unused tail
            pending = null;
        }

        void Encode(PxarFileNode file)
        {
            EmitPending();
            offsets[file] = pos;
            segments.Add(new EncodeFileSegment(file));
            pos += PxarFormat.HeaderSize + file.Length;
            filesReencoded++;
            bytesReencoded += PxarFormat.HeaderSize + file.Length;
        }

        // keepLast: the next file is reusable but not contiguous (a hole) — hold the last chunk back.
        void Flush(bool keepLast)
        {
            if (run.Count == 0) return;
            var (chunks, sizes, padStart, padEnd) = Lookup(previousChunks!, runStart, runEnd);
            bool shared = pending is { } p0 && p0.Chunk.End == chunks[0].End;
            long deadStart = shared ? runStart - pending!.Value.UsedEnd : padStart;
            long runPadding = deadStart + padEnd;
            if ((double)runPadding / ((runEnd - runStart) + runPadding) > PaddingThreshold)
            {
                foreach (var (file, _) in run) Encode(file);
                run.Clear();
                return;
            }

            if (!shared) EmitPending();
            // chunks[0] starts at `pos` (a shared pending chunk sits exactly there too)
            long basePos = pos + padStart;
            foreach (var (file, prevOffset) in run)
            {
                offsets[file] = basePos + (prevOffset - runStart);
                filesReused++;
                bytesReused += PxarFormat.HeaderSize + file.Length;
            }
            padding += deadStart;

            int emit = keepLast ? chunks.Count - 1 : chunks.Count;
            if (emit > 0)
            {
                segments.Add(new InjectChunksSegment(chunks.GetRange(0, emit), sizes.GetRange(0, emit)));
                for (int i = 0; i < emit; i++) pos += sizes[i];
                chunksInjected += emit;
            }
            pending = keepLast ? (chunks[^1], sizes[^1], runEnd) : null;
            if (!keepLast) padding += padEnd;
            run.Clear();
        }

        foreach (var (file, relPath) in files)
        {
            long? prev = canReuse ? ReusableOffset(file, relPath, previousIndex!, previousEnd, ref lastAccepted) : null;
            if (prev is not { } prevOffset)
            {
                Flush(keepLast: false);
                Encode(file);
                continue;
            }

            long start = prevOffset, end = prevOffset + PxarFormat.HeaderSize + file.Length;
            if (run.Count > 0 && runEnd != start) Flush(keepLast: true);
            if (run.Count == 0) runStart = start;
            runEnd = end;
            run.Add((file, prevOffset));
        }
        Flush(keepLast: false);
        EmitPending();

        return new PayloadPlan(offsets, segments, pos,
            new PayloadPlanStats(filesReused, bytesReused, filesReencoded, bytesReencoded, chunksInjected, padding));
    }

    // Previous PAYLOAD header offset when `file` is unchanged and reusable, else null.
    private static long? ReusableOffset(PxarFileNode file, string relPath, IReadOnlyDictionary<string, PxarEntryLocation> previous,
        long previousEnd, ref long? lastAccepted)
    {
        if (!previous.TryGetValue(relPath, out var p) || p.Kind != PxarEntryKind.File || !p.InPayloadArchive)
            return null;
        if (p.PayloadLength != file.Length || p.Mode != (uint)PxarEncoder.ModeOf(file) ||
            (long)p.MtimeSecs != file.MtimeSecs || p.MtimeNanos != file.MtimeNanos)
            return null;
        long offset = p.PayloadRefOffset;
        if (offset < PxarFormat.HeaderSize || offset + PxarFormat.HeaderSize + p.PayloadLength > previousEnd)
            return null; // reference outside the previous payload archive: corrupt or mismatched pair
        if (lastAccepted is { } last && offset <= last)
            return null; // duplicate / non-monotonic previous offsets: re-encode (self-heals, as upstream)
        lastAccepted = offset;
        return offset;
    }

    // The previous chunks covering [start, end), with the bytes before `start` in the first chunk and
    // after `end` in the last one.
    public static (List<PreviousPayloadChunk> Chunks, List<long> Sizes, long PadStart, long PadEnd) Lookup(
        IReadOnlyList<PreviousPayloadChunk> chunks, long start, long end)
    {
        // first chunk whose end is past `start`
        int lo = 0, hi = chunks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (chunks[mid].End > start) hi = mid; else lo = mid + 1;
        }

        var result = new List<PreviousPayloadChunk>();
        var sizes = new List<long>();
        long chunkStart = lo == 0 ? 0 : chunks[lo - 1].End;
        long padStart = start - chunkStart;
        long prevEnd = chunkStart;
        for (int i = lo; i < chunks.Count; i++)
        {
            result.Add(chunks[i]);
            sizes.Add(chunks[i].End - prevEnd);
            prevEnd = chunks[i].End;
            if (chunks[i].End >= end) break;
        }
        return (result, sizes, padStart, prevEnd - end);
    }
}
