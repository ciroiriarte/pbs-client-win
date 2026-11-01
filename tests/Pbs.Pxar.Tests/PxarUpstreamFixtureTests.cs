using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Pbs.Pxar;

namespace Pbs.Pxar.Tests;

/// <summary>
/// Ground-truth interop (issue #33): archives produced by the upstream <c>pxar</c> tool (PBS 4.2.5),
/// not by this client. Generated on a Linux host from this tree:
/// <code>
/// a.txt            "hello world\n"  + xattr user.comment=xattr-value
/// empty.bin        (0 bytes)
/// fifo1            FIFO
/// hard-a           hardlink to a.txt
/// link-to-a        symlink -> a.txt
/// sub/             + xattr user.dir=1
/// sub/random.bin   70000 random bytes
/// sub/deeper/d.txt "deep file content\n" (mtime 2024-01-02 03:04:05.123456789 local)
/// sub/ñandú-日本.txt "unicode\n"
/// </code>
/// with <c>pxar create unified.pxar src</c> and
/// <c>pxar create split.mpxar src --payload-output split.ppxar</c>.
/// </summary>
public class PxarUpstreamFixtureTests
{
    private const string RandomBinSha256 = "bdfb25bfcade5d8dd802011fd750485c7e55cbf30cc50e36fe8975103c9f5fd7";
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static FileStream Open(string name) => File.OpenRead(Path.Combine(Fixtures, name));

    [Fact]
    public async Task Extract_Unified_RestoresAllRegularContent()
    {
        using var dir = new TempDir();
        await using var pxar = Open("unified.pxar");
        var result = await PxarDecoder.ExtractAsync(pxar, dir.Path);
        AssertTree(dir.Path, result);
    }

    [Fact]
    public async Task Extract_Split_RestoresAllRegularContent()
    {
        using var dir = new TempDir();
        await using var meta = Open("split.mpxar");
        await using var payload = Open("split.ppxar");
        var result = await PxarDecoder.ExtractSplitAsync(meta, payload, dir.Path, includePatterns: null);
        AssertTree(dir.Path, result);
    }

    [Fact]
    public async Task Extract_Split_NonSeekableStreams_Work()
    {
        using var dir = new TempDir();
        await using var meta = new NonSeekable(Open("split.mpxar"));
        await using var payload = new NonSeekable(Open("split.ppxar"));
        var result = await PxarDecoder.ExtractSplitAsync(meta, payload, dir.Path, includePatterns: null);
        AssertTree(dir.Path, result);
    }

    [Fact]
    public async Task Extract_Split_WithPatterns_SkipsUnmatchedPayloads()
    {
        using var dir = new TempDir();
        await using var meta = Open("split.mpxar");
        await using var payload = Open("split.ppxar");
        await PxarDecoder.ExtractSplitAsync(meta, payload, dir.Path, new[] { "sub/deeper/**" });

        Assert.Equal("deep file content\n", File.ReadAllText(Path.Combine(dir.Path, "sub", "deeper", "d.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "sub", "random.bin")));
    }

    [Fact]
    public async Task Extract_SplitMetadataWithoutPayload_FailsClearly()
    {
        using var dir = new TempDir();
        await using var meta = Open("split.mpxar");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PxarDecoder.ExtractAsync(meta, dir.Path));
        Assert.Contains("payload stream", ex.Message);
    }

    [Fact]
    public async Task Extract_PayloadStreamWithoutStartMarker_IsRejected()
    {
        using var dir = new TempDir();
        await using var meta = Open("split.mpxar");
        await using var wrongPayload = Open("unified.pxar");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => PxarDecoder.ExtractSplitAsync(meta, wrongPayload, dir.Path, includePatterns: null));
    }

    [Fact]
    public async Task Extract_PayloadRefMovingBackwards_IsRejected()
    {
        // Rewrite the 2nd PAYLOAD_REF offset to point before the 1st one.
        var meta = File.ReadAllBytes(Path.Combine(Fixtures, "split.mpxar"));
        var refs = FindRecords(meta, PxarFormat.PayloadRef);
        Assert.True(refs.Count >= 2);
        BinaryPrimitives.WriteUInt64LittleEndian(meta.AsSpan(refs[1] + 16, 8), 0);

        using var dir = new TempDir();
        await using var payload = Open("split.ppxar");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => PxarDecoder.ExtractSplitAsync(new MemoryStream(meta), payload, dir.Path, includePatterns: null));
        Assert.Contains("behind", ex.Message);
    }

    [Fact]
    public async Task Index_Split_LocatesPayloadsInPayloadStream()
    {
        await using var meta = Open("split.mpxar");
        var index = await PxarIndexer.BuildIndexAsync(meta);
        var byPath = index.ToDictionary(e => e.RelativePath);

        var a = byPath["a.txt"];
        Assert.Equal(PxarEntryKind.File, a.Kind);
        Assert.True(a.InPayloadArchive);
        Assert.Equal(12, a.PayloadLength);

        // hardlink resolves to its target's payload; FIFO stays special
        Assert.Equal(a with { RelativePath = "hard-a" }, byPath["hard-a"]);
        Assert.Equal(PxarEntryKind.Special, byPath["fifo1"].Kind);
        Assert.Equal(PxarEntryKind.Symlink, byPath["link-to-a"].Kind);
        Assert.Equal(123456789u, byPath["sub/deeper/d.txt"].MtimeNanos);

        await using var payload = Open("split.ppxar");
        Assert.Equal("hello world\n", ReadAt(payload, a));
        Assert.Equal("unicode\n", ReadAt(payload, byPath["sub/ñandú-日本.txt"]));
        Assert.Equal(RandomBinSha256, Sha256(ReadBytesAt(payload, byPath["sub/random.bin"])));
    }

    [Fact]
    public async Task Index_Unified_HandlesUpstreamRecords()
    {
        await using var pxar = Open("unified.pxar");
        var index = await PxarIndexer.BuildIndexAsync(pxar);
        var byPath = index.ToDictionary(e => e.RelativePath);
        Assert.False(byPath["a.txt"].InPayloadArchive);
        Assert.Equal("hello world\n", ReadAt(pxar, byPath["hard-a"]));
        Assert.Equal(RandomBinSha256, Sha256(ReadBytesAt(pxar, byPath["sub/random.bin"])));
    }

    [Fact]
    public async Task MountModel_Split_ServesFilesFromCatalogBuiltFromIndex()
    {
        await using var meta = Open("split.mpxar");
        await using var payload = Open("split.ppxar");
        var index = await PxarIndexer.BuildIndexAsync(meta);
        var catalog = PxarCatalog.FromIndex(index);

        Assert.Equal(new[] { "a.txt", "empty.bin", "fifo1", "hard-a", "link-to-a", "sub" },
            catalog.Children.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        var sub = catalog.Children.Single(c => c.Name == "sub");
        Assert.Contains(sub.Children, c => c.Name == "deeper" && c.Type == PxarCatalogEntryType.Directory);
        Assert.Equal(PxarCatalogEntryType.Fifo, catalog.Children.Single(c => c.Name == "fifo1").Type);
        Assert.Equal(70000, sub.Children.Single(c => c.Name == "random.bin").Size);

        var model = new PxarMountModel(catalog, meta, index, payload);
        Assert.True(model.TryGetFileLocation("sub/deeper/d.txt", out var loc));
        var buffer = new byte[64];
        int n = model.ReadFile(loc, 5, buffer, 0, buffer.Length);
        Assert.Equal("file content\n", Encoding.UTF8.GetString(buffer, 0, n));
    }

    [Fact]
    public async Task MountModel_SplitIndexWithoutPayloadStream_IsRejected()
    {
        await using var meta = Open("split.mpxar");
        var index = await PxarIndexer.BuildIndexAsync(meta);
        Assert.Throws<ArgumentException>(() => new PxarMountModel(PxarCatalog.FromIndex(index), meta, index));
    }

    private static void AssertTree(string root, PxarExtractResult result)
    {
        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "a.txt")));
        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "hard-a")));
        Assert.Equal(0, new FileInfo(Path.Combine(root, "empty.bin")).Length);
        Assert.Equal("deep file content\n", File.ReadAllText(Path.Combine(root, "sub", "deeper", "d.txt")));
        Assert.Equal("unicode\n", File.ReadAllText(Path.Combine(root, "sub", "ñandú-日本.txt")));
        Assert.Equal(RandomBinSha256, Sha256(File.ReadAllBytes(Path.Combine(root, "sub", "random.bin"))));
        Assert.False(File.Exists(Path.Combine(root, "fifo1")));
        if (!OperatingSystem.IsWindows()) // symlink creation needs a privilege on Windows
            Assert.Equal("a.txt", new FileInfo(Path.Combine(root, "link-to-a")).LinkTarget);

        Assert.Equal(new PxarExtractResult(Files: 5, HardlinksCopied: 1, HardlinksSkipped: 0, SpecialSkipped: 1), result);
    }

    private static string ReadAt(Stream s, PxarEntryLocation loc) => Encoding.UTF8.GetString(ReadBytesAt(s, loc));

    private static byte[] ReadBytesAt(Stream s, PxarEntryLocation loc)
    {
        var buffer = new byte[loc.PayloadLength];
        s.Position = loc.PayloadOffset;
        s.ReadExactly(buffer);
        return buffer;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // Offsets of records with the given htype, walking the archive header by header.
    private static List<int> FindRecords(byte[] archive, ulong htype)
    {
        var found = new List<int>();
        int pos = 0;
        while (pos + 16 <= archive.Length)
        {
            var h = BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(pos, 8));
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(pos + 8, 8));
            if (h == htype) found.Add(pos);
            // ENTRY/structural records are walked into; everything is header+body except GOODBYE,
            // which is also header+body, so a flat walk works for a metadata-only stream.
            pos += (int)size;
        }
        return found;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pxar-fixture-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
