using System.IO.Pipelines;
using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;
using Pbs.Pxar;

namespace Pbs.Cli;

/// <summary>
/// Uploads a split archive's payload stream (<c>.ppxar</c>, issue #33) as a dynamic index. Plain bytes
/// are content-defined chunked, deduplicated and uploaded like any archive. An injection first ends
/// the current chunk (a forced boundary, so the next byte starts a new chunk), then appends the
/// previous snapshot's chunks to the index by digest — no data is read or uploaded for them. The
/// server accepts those references because downloading the previous payload index registered its
/// chunks as known for this session.
/// </summary>
public sealed class PayloadUploader : IPayloadSink
{
    private readonly BackupSession _session;
    private readonly string _storedName;
    private readonly long _wid;
    private readonly CryptConfig? _crypt;
    private readonly ChunkerOptions? _options;
    private readonly HashSet<ChunkDigest> _known;
    private readonly IReadOnlyList<long>? _suggested;
    private readonly HashSet<ChunkDigest> _seen = new();
    private readonly List<ChunkRef> _chunks = new();
    private long _offset;
    private int _uploaded, _reused, _injected;

    // current byte stretch: the writer side feeds a pipe the chunker consumes on a background task
    private Pipe? _pipe;
    private Task? _consumer;
    private CancellationToken _ct;

    private PayloadUploader(BackupSession session, string storedName, long wid, CryptConfig? crypt, ChunkerOptions? options,
        HashSet<ChunkDigest> known, IReadOnlyList<long>? suggested)
    {
        _suggested = suggested;
        _session = session;
        _storedName = storedName;
        _wid = wid;
        _crypt = crypt;
        _options = options;
        _known = known;
    }

    /// <param name="known">Digests the server already holds (from the previous payload index).</param>
    /// <param name="suggestedBoundaries">Ascending payload positions where chunks should preferably end
    /// (the starts of newly encoded files), see <see cref="ContentDefinedChunker"/>.</param>
    public static async Task<PayloadUploader> StartAsync(
        BackupSession session, string storedName, IEnumerable<ChunkDigest> known,
        IReadOnlyList<long>? suggestedBoundaries = null, CryptConfig? crypt = null, ChunkerOptions? options = null, CancellationToken ct = default)
    {
        var wid = await session.CreateDynamicIndexAsync(storedName, ct);
        return new PayloadUploader(session, storedName, wid, crypt, options, new HashSet<ChunkDigest>(known), suggestedBoundaries) { _ct = ct };
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_pipe is null)
        {
            _pipe = new Pipe();
            var pipe = _pipe;
            var basePosition = _offset; // payload position of this stretch's first byte
            _consumer = Task.Run(() => ConsumeAsync(pipe, basePosition, _ct));
        }
        var result = await _pipe.Writer.WriteAsync(data, ct);
        if (result.IsCompleted)
        {
            await _consumer!; // the chunker/uploader stopped early: surface its error
            throw new InvalidOperationException("payload upload stopped before the stream ended");
        }
    }

    public async ValueTask InjectAsync(InjectChunksSegment segment, CancellationToken ct)
    {
        await EndStretchAsync(); // forced chunk boundary before the injected chunks
        for (int i = 0; i < segment.Chunks.Count; i++)
        {
            var size = checked((int)segment.Sizes[i]);
            var digest = ChunkDigest.FromBytes(segment.Chunks[i].Digest);
            if (!_known.Contains(digest))
                throw new InvalidOperationException($"injected chunk {digest} is not known to the server");
            _chunks.Add(new ChunkRef(_offset, size, digest));
            _offset += size;
            _reused++;
            _injected++;
        }
    }

    /// <summary>Finish the stream, register and close the index.</summary>
    public async Task<(DynamicArchive.UploadStats Stats, int Injected)> FinishAsync(CancellationToken ct = default)
    {
        await EndStretchAsync();
        await _session.AppendDynamicIndexAsync(_wid, _chunks, ct);
        await _session.CloseDynamicIndexAsync(_storedName, _wid, _chunks.Count, _offset,
            DynamicIndex.ComputeCsum(_chunks), _crypt is null ? "none" : "encrypt", ct);
        return (new DynamicArchive.UploadStats(_chunks.Count, _uploaded, _reused, _offset), _injected);
    }

    private async Task EndStretchAsync()
    {
        if (_pipe is null) return;
        await _pipe.Writer.CompleteAsync();
        await _consumer!;
        _pipe = null;
        _consumer = null;
    }

    private async Task ConsumeAsync(Pipe pipe, long basePosition, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            await using var reader = pipe.Reader.AsStream(leaveOpen: true);
            await foreach (var slice in new ContentDefinedChunker(_options).SplitStreamAsync(reader, _suggested, basePosition, ct))
            {
                var digest = _crypt is null ? ChunkDigest.Compute(slice) : ChunkDigest.FromBytes(_crypt.ComputeDigest(slice));
                var chunk = new ChunkRef(_offset, slice.Length, digest);
                _offset += slice.Length;
                _chunks.Add(chunk);

                if (_known.Contains(digest)) { _reused++; continue; }
                if (!_seen.Add(digest)) continue;
                var blob = _crypt is null ? DataBlob.Encode(slice, compress: true) : _crypt.EncryptBlob(slice, compress: true);
                await _session.UploadDynamicChunkAsync(_wid, chunk, blob, ct);
                _uploaded++;
            }
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            await pipe.Reader.CompleteAsync(error);
        }
    }
}
