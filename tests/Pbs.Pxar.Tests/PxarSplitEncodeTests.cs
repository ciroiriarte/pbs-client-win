using System.Security.Cryptography;
using System.Text;
using Pbs.Pxar;

namespace Pbs.Pxar.Tests;

/// <summary>
/// Split-archive encoding (issue #33): data mode (every payload encoded) and metadata mode (unchanged
/// files reuse the previous snapshot's payload chunks). An in-memory sink stands in for the server:
/// an injected chunk becomes the corresponding byte range of the previous payload stream, which is
/// exactly what the index reassembles on restore.
/// </summary>
public class PxarSplitEncodeTests : IDisposable
{
    private readonly string _src = Path.Combine(Path.GetTempPath(), "pxar-split-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _dst = Path.Combine(Path.GetTempPath(), "pxar-split-dst-" + Guid.NewGuid().ToString("N"));

    public PxarSplitEncodeTests()
    {
        Directory.CreateDirectory(Path.Combine(_src, "big"));
        Directory.CreateDirectory(Path.Combine(_src, "small"));
        var rng = new Random(33);
        for (int i = 0; i < 12; i++)
        {
            var data = new byte[20_000 + i * 1000];
            rng.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_src, "big", $"f{i:D2}.bin"), data);
        }
        for (int i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(_src, "small", $"s{i:D2}.txt"), $"small-{i}\n");
        File.WriteAllBytes(Path.Combine(_src, "empty.bin"), Array.Empty<byte>());
    }

    public void Dispose()
    {
        foreach (var d in new[] { _src, _dst })
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    [Fact]
    public async Task DataMode_RoundTrips()
    {
        var (meta, payload, plan) = await EncodeAsync(PxarTree.FromDirectory(_src), previous: null);
        Assert.Equal(0, plan.Stats.FilesReused);
        Assert.Equal(43, plan.Stats.FilesReencoded);

        await PxarDecoder.ExtractSplitAsync(new MemoryStream(meta), new MemoryStream(payload), _dst, null);
        AssertSameTree(_src, _dst);
    }

    [Fact]
    public async Task MetadataMode_ReusesUnchangedPayloads_AndRoundTrips()
    {
        var first = await EncodeAsync(PxarTree.FromDirectory(_src), previous: null);

        // change one file (content + mtime), delete one, add one
        var rng = new Random(7);
        var changed = new byte[25_000];
        rng.NextBytes(changed);
        File.WriteAllBytes(Path.Combine(_src, "big", "f05.bin"), changed);
        File.SetLastWriteTimeUtc(Path.Combine(_src, "big", "f05.bin"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(_src, "big", "f09.bin"));
        File.WriteAllText(Path.Combine(_src, "big", "new.txt"), "new file\n");

        var previous = await PreviousAsync(first.Meta, first.Payload, chunkSize: 4096);
        var second = await EncodeAsync(PxarTree.FromDirectory(_src), previous);

        // The 10 unchanged big files are reused (runs broken by the changed f05 and the deleted f09).
        // The 30 tiny small/* files form a <1 KiB run sharing a 4 KiB chunk with the end of f11: the
        // padding exceeds 10%, so they are cheaply re-encoded instead — upstream's rule.
        Assert.Equal(10, second.Plan.Stats.FilesReused);
        Assert.True(second.Plan.Stats.ChunksInjected > 0);
        // payload bytes reused vs. the whole previous payload: most of it
        Assert.True(second.Plan.Stats.BytesReused > first.Payload.Length / 2);

        await PxarDecoder.ExtractSplitAsync(new MemoryStream(second.Meta), new MemoryStream(second.Payload), _dst, null);
        AssertSameTree(_src, _dst);

        // the new archive also indexes correctly (mount path)
        var index = await PxarIndexer.BuildIndexAsync(new MemoryStream(second.Meta));
        var f00 = index.Single(e => e.RelativePath == "big/f00.bin");
        var model = new PxarMountModel(PxarCatalog.FromIndex(index), new MemoryStream(second.Meta), index, new MemoryStream(second.Payload));
        var buffer = new byte[f00.PayloadLength];
        Assert.Equal(buffer.Length, model.ReadFile(f00, 0, buffer, 0, buffer.Length));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_src, "big", "f00.bin")), buffer);
    }

    [Fact]
    public async Task MetadataMode_WithNothingChanged_ReusesEverything()
    {
        var first = await EncodeAsync(PxarTree.FromDirectory(_src), previous: null);
        var previous = await PreviousAsync(first.Meta, first.Payload, chunkSize: 4096);
        var second = await EncodeAsync(PxarTree.FromDirectory(_src), previous);

        Assert.Equal(43, second.Plan.Stats.FilesReused);
        Assert.Equal(0, second.Plan.Stats.FilesReencoded);
        // only the start and tail markers, which sit inside the first and last injected chunk
        Assert.Equal(2 * PxarFormat.HeaderSize, second.Plan.Stats.PaddingBytes);
        Assert.Single(second.Plan.Segments);
        await PxarDecoder.ExtractSplitAsync(new MemoryStream(second.Meta), new MemoryStream(second.Payload), _dst, null);
        AssertSameTree(_src, _dst);
    }

    [Fact]
    public async Task MetadataMode_HoleFromDeletedFile_SharesBoundaryChunk_AndReusesTheRest()
    {
        var first = await EncodeAsync(PxarTree.FromDirectory(_src), previous: null);
        File.Delete(Path.Combine(_src, "small", "s10.txt")); // a ~25-byte hole inside one 4 KiB chunk

        var previous = await PreviousAsync(first.Meta, first.Payload, chunkSize: 4096);
        var second = await EncodeAsync(PxarTree.FromDirectory(_src), previous);

        // Without sharing, the run after the hole would start a whole chunk early (the previous run's
        // bytes counted as padding) and be re-encoded; with it only the hole is dead weight.
        Assert.Equal(42, second.Plan.Stats.FilesReused);
        Assert.Equal(0, second.Plan.Stats.FilesReencoded);
        Assert.True(second.Plan.Stats.PaddingBytes < 100, $"padding {second.Plan.Stats.PaddingBytes}");

        await PxarDecoder.ExtractSplitAsync(new MemoryStream(second.Meta), new MemoryStream(second.Payload), _dst, null);
        AssertSameTree(_src, _dst);
    }

    [Fact]
    public async Task MetadataMode_HugeChunks_ExceedPaddingThreshold_AndReencode()
    {
        var first = await EncodeAsync(PxarTree.FromDirectory(_src), previous: null);
        // touch every 3rd small file so runs are short, and use one chunk for the whole stream:
        // each run would drag the entire previous payload along as padding.
        for (int i = 0; i < 30; i += 3)
            File.SetLastWriteTimeUtc(Path.Combine(_src, "small", $"s{i:D2}.txt"), DateTime.UtcNow.AddMinutes(5));
        File.SetLastWriteTimeUtc(Path.Combine(_src, "empty.bin"), DateTime.UtcNow.AddMinutes(5));
        foreach (var f in Directory.GetFiles(Path.Combine(_src, "big")).Where((_, i) => i % 2 == 0))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(5));

        var previous = await PreviousAsync(first.Meta, first.Payload, chunkSize: int.MaxValue);
        var second = await EncodeAsync(PxarTree.FromDirectory(_src), previous);

        Assert.Equal(0, second.Plan.Stats.FilesReused);
        Assert.Equal(0, second.Plan.Stats.ChunksInjected);
        await PxarDecoder.ExtractSplitAsync(new MemoryStream(second.Meta), new MemoryStream(second.Payload), _dst, null);
        AssertSameTree(_src, _dst);
    }

    [Fact]
    public void Lookup_ReturnsCoveringChunksAndPadding()
    {
        var chunks = new[] { Chunk(100), Chunk(250), Chunk(400), Chunk(1000) };
        var (picked, sizes, padStart, padEnd) = PxarPayloadPlanner.Lookup(chunks, 120, 400);
        Assert.Equal(new long[] { 250, 400 }, picked.Select(c => c.End));
        Assert.Equal(new long[] { 150, 150 }, sizes);
        Assert.Equal(20, padStart);
        Assert.Equal(0, padEnd);

        (picked, sizes, padStart, padEnd) = PxarPayloadPlanner.Lookup(chunks, 0, 50);
        Assert.Equal(new long[] { 100 }, picked.Select(c => c.End));
        Assert.Equal((0L, 50L), (padStart, padEnd));

        (picked, _, padStart, padEnd) = PxarPayloadPlanner.Lookup(chunks, 400, 401);
        Assert.Equal(new long[] { 1000 }, picked.Select(c => c.End));
        Assert.Equal((0L, 599L), (padStart, padEnd));
    }

    [Fact]
    public void Plan_NonMonotonicPreviousOffsets_AreReencoded()
    {
        var a = FileNode("a", 10);
        var b = FileNode("b", 10);
        var previous = new Dictionary<string, PxarEntryLocation>
        {
            // b's previous offset is before a's: a "duplicate/non-monotonic" previous archive
            ["a"] = Location("a", refOffset: 42, a),
            ["b"] = Location("b", refOffset: 16, b),
        };
        var plan = PxarPayloadPlanner.Plan(new[] { (a, "a"), (b, "b") }, previous, new[] { Chunk(16), Chunk(42), Chunk(68) });
        Assert.Equal(1, plan.Stats.FilesReused);
        Assert.Equal(1, plan.Stats.FilesReencoded);
        Assert.True(plan.OffsetOf(b) > plan.OffsetOf(a));
    }

    [Fact]
    public void Plan_PreviousReferenceOutsidePayloadArchive_IsReencoded()
    {
        var a = FileNode("a", 10);
        var previous = new Dictionary<string, PxarEntryLocation> { ["a"] = Location("a", refOffset: 1000, a) };
        var plan = PxarPayloadPlanner.Plan(new[] { (a, "a") }, previous, new[] { Chunk(100) });
        Assert.Equal(0, plan.Stats.FilesReused);
    }

    // ---- helpers ----

    private sealed record Previous(Dictionary<string, PxarEntryLocation> Index, List<PreviousPayloadChunk> Chunks, byte[] Payload);

    private static async Task<(byte[] Meta, byte[] Payload, PayloadPlan Plan)> EncodeAsync(PxarDirNode tree, Previous? previous)
    {
        var plan = PxarPayloadPlanner.Plan(PxarEncoder.FilesInEncodeOrder(tree), previous?.Index, previous?.Chunks);
        var meta = new MemoryStream();
        await PxarEncoder.EncodeMetadataAsync(tree, meta, plan);
        var sink = new MemorySink(previous?.Payload, previous?.Chunks);
        await PxarPayloadWriter.WriteAsync(plan, sink);
        return (meta.ToArray(), sink.Output.ToArray(), plan);
    }

    // Index the previous metadata stream and cut the previous payload into fixed-size "chunks".
    private static async Task<Previous> PreviousAsync(byte[] meta, byte[] payload, int chunkSize)
    {
        var index = (await PxarIndexer.BuildIndexAsync(new MemoryStream(meta))).ToDictionary(e => e.RelativePath);
        var chunks = new List<PreviousPayloadChunk>();
        for (long end = Math.Min(chunkSize, payload.Length); ; end = Math.Min(end + chunkSize, payload.Length))
        {
            chunks.Add(new PreviousPayloadChunk(end, SHA256.HashData(BitConverter.GetBytes(end))));
            if (end == payload.Length) break;
        }
        return new Previous(index, chunks, payload);
    }

    private sealed class MemorySink(byte[]? previousPayload, List<PreviousPayloadChunk>? previousChunks) : IPayloadSink
    {
        public MemoryStream Output { get; } = new();

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            Output.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask InjectAsync(InjectChunksSegment segment, CancellationToken ct)
        {
            foreach (var chunk in segment.Chunks)
            {
                int i = previousChunks!.FindIndex(c => c.End == chunk.End);
                long start = i == 0 ? 0 : previousChunks[i - 1].End;
                Output.Write(previousPayload!, (int)start, (int)(chunk.End - start));
            }
            return ValueTask.CompletedTask;
        }
    }

    private static PreviousPayloadChunk Chunk(long end) => new(end, new byte[32]);

    private static PxarFileNode FileNode(string name, long length) =>
        new() { Name = name, Length = length, MtimeSecs = 1000, OpenRead = () => new MemoryStream(new byte[length]) };

    private static PxarEntryLocation Location(string path, long refOffset, PxarFileNode f) =>
        new(path, PxarEntryKind.File, refOffset + PxarFormat.HeaderSize, f.Length, (uint)PxarFormat.ModeFile,
            (ulong)f.MtimeSecs, InPayloadArchive: true, MtimeNanos: f.MtimeNanos);

    private static void AssertSameTree(string expected, string actual)
    {
        var e = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(expected, p)).Order().ToList();
        var a = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(actual, p)).Order().ToList();
        Assert.Equal(e, a);
        foreach (var rel in e)
            Assert.True(File.ReadAllBytes(Path.Combine(expected, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actual, rel))), rel);
    }
}
