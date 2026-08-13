using System.Text;
using Pbs.Pxar;

public class PxarMountModelTests
{
    // A small archive laid out in one content buffer:
    //   root/
    //     alpha.txt      -> "alpha-payload"
    //     nested/
    //       beta.bin     -> 0..255 (256 bytes)
    //       link         -> symlink (no payload)
    //     empty.txt      -> "" (zero-length payload)
    private static (PxarMountModel Model, byte[] Alpha, byte[] Beta) BuildModel()
    {
        var alpha = Encoding.UTF8.GetBytes("alpha-payload");
        var beta = new byte[256];
        for (var i = 0; i < beta.Length; i++)
            beta[i] = (byte)i;

        // content = [alpha][beta]; empty.txt has zero length so any offset works.
        var content = new MemoryStream();
        var alphaOffset = content.Position;
        content.Write(alpha);
        var betaOffset = content.Position;
        content.Write(beta);
        content.Position = 0;

        var catalog = new PxarCatalogEntry(string.Empty, PxarCatalogEntryType.Directory, 0, 0, new[]
        {
            new PxarCatalogEntry("alpha.txt", PxarCatalogEntryType.File, alpha.Length, 111, Array.Empty<PxarCatalogEntry>()),
            new PxarCatalogEntry("nested", PxarCatalogEntryType.Directory, 0, 222, new[]
            {
                new PxarCatalogEntry("beta.bin", PxarCatalogEntryType.File, beta.Length, 333, Array.Empty<PxarCatalogEntry>()),
                new PxarCatalogEntry("link", PxarCatalogEntryType.Symlink, 0, 444, Array.Empty<PxarCatalogEntry>()),
            }),
            new PxarCatalogEntry("empty.txt", PxarCatalogEntryType.File, 0, 555, Array.Empty<PxarCatalogEntry>()),
        });

        var index = new[]
        {
            new PxarEntryLocation(string.Empty, PxarEntryKind.Directory, 0, 0, 0, 0),
            new PxarEntryLocation("alpha.txt", PxarEntryKind.File, alphaOffset, alpha.Length, 0, 111),
            new PxarEntryLocation("nested", PxarEntryKind.Directory, 0, 0, 0, 222),
            new PxarEntryLocation("nested/beta.bin", PxarEntryKind.File, betaOffset, beta.Length, 0, 333),
            new PxarEntryLocation("nested/link", PxarEntryKind.Symlink, 0, 0, 0, 444),
            new PxarEntryLocation("empty.txt", PxarEntryKind.File, 0, 0, 0, 555),
        };

        return (new PxarMountModel(catalog, content, index), alpha, beta);
    }

    [Fact]
    public void Ctor_RejectsNonSeekableStream()
    {
        var catalog = new PxarCatalogEntry(string.Empty, PxarCatalogEntryType.Directory, 0, 0, Array.Empty<PxarCatalogEntry>());
        Assert.Throws<ArgumentException>(() =>
            new PxarMountModel(catalog, new NonSeekableStream(), Array.Empty<PxarEntryLocation>()));
    }

    [Fact]
    public void TryGetEntry_ResolvesRootDirsAndNestedPaths()
    {
        var (model, _, _) = BuildModel();

        Assert.True(model.TryGetEntry("", out var root));
        Assert.Equal(PxarCatalogEntryType.Directory, root.Type);
        Assert.Equal(3, root.Children.Count);

        Assert.True(model.TryGetEntry("nested", out var nested));
        Assert.Equal(PxarCatalogEntryType.Directory, nested.Type);

        Assert.True(model.TryGetEntry("nested/beta.bin", out var beta));
        Assert.Equal(256, beta.Size);

        // Backslashes and stray slashes normalize to the same key.
        Assert.True(model.TryGetEntry(@"\nested\beta.bin\", out var betaAlt));
        Assert.Equal(beta.Size, betaAlt.Size);

        Assert.False(model.TryGetEntry("does/not/exist", out _));
    }

    [Fact]
    public void TryGetFileLocation_OnlyMatchesFiles()
    {
        var (model, _, _) = BuildModel();

        Assert.True(model.TryGetFileLocation("alpha.txt", out var alpha));
        Assert.Equal(PxarEntryKind.File, alpha.Kind);

        // Directories and symlinks are not file payloads.
        Assert.False(model.TryGetFileLocation("nested", out _));
        Assert.False(model.TryGetFileLocation("nested/link", out _));
        Assert.False(model.TryGetFileLocation("missing.txt", out _));
    }

    [Fact]
    public void ReadFile_ReturnsFullPayload()
    {
        var (model, alpha, _) = BuildModel();
        Assert.True(model.TryGetFileLocation("alpha.txt", out var loc));

        var buffer = new byte[alpha.Length];
        var read = model.ReadFile(loc, 0, buffer, 0, buffer.Length);

        Assert.Equal(alpha.Length, read);
        Assert.Equal(alpha, buffer);
    }

    [Fact]
    public void ReadFile_HonorsOffsetAndClampsToPayloadLength()
    {
        var (model, _, beta) = BuildModel();
        Assert.True(model.TryGetFileLocation("nested/beta.bin", out var loc));

        // Read a middle window.
        var window = new byte[64];
        var read = model.ReadFile(loc, 100, window, 0, window.Length);
        Assert.Equal(64, read);
        Assert.Equal(beta.AsSpan(100, 64).ToArray(), window);

        // Ask for more than remains: clamp to payload length, do not read past it into neighbouring content.
        var tail = new byte[128];
        var tailRead = model.ReadFile(loc, 200, tail, 0, tail.Length);
        Assert.Equal(56, tailRead); // 256 - 200
        Assert.Equal(beta.AsSpan(200, 56).ToArray(), tail.AsSpan(0, 56).ToArray());
    }

    [Fact]
    public void ReadFile_AtOrPastEof_ReturnsZero()
    {
        var (model, _, _) = BuildModel();
        Assert.True(model.TryGetFileLocation("nested/beta.bin", out var loc));

        var buffer = new byte[16];
        Assert.Equal(0, model.ReadFile(loc, 256, buffer, 0, buffer.Length));
        Assert.Equal(0, model.ReadFile(loc, 999, buffer, 0, buffer.Length));

        // Zero-length file reads zero from offset 0.
        Assert.True(model.TryGetFileLocation("empty.txt", out var empty));
        Assert.Equal(0, model.ReadFile(empty, 0, buffer, 0, buffer.Length));
    }

    [Fact]
    public void ReadFile_WritesAtBufferOffset()
    {
        var (model, alpha, _) = BuildModel();
        Assert.True(model.TryGetFileLocation("alpha.txt", out var loc));

        var buffer = new byte[alpha.Length + 4];
        buffer[0] = 0xEE;
        var read = model.ReadFile(loc, 0, buffer, 2, alpha.Length);

        Assert.Equal(alpha.Length, read);
        Assert.Equal(0xEE, buffer[0]);
        Assert.Equal(alpha, buffer.AsSpan(2, alpha.Length).ToArray());
    }

    [Fact]
    public void ReadFile_RejectsBadArguments()
    {
        var (model, _, _) = BuildModel();
        Assert.True(model.TryGetFileLocation("alpha.txt", out var loc));
        var buffer = new byte[8];

        Assert.Throws<ArgumentOutOfRangeException>(() => model.ReadFile(loc, -1, buffer, 0, buffer.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.ReadFile(loc, 0, buffer, 0, buffer.Length + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.ReadFile(loc, 0, buffer, 6, 4));
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
