using System.Text;
using Pbs.Pxar;

public class PxarIndexerTests
{
    [Fact]
    public async Task BuildIndexAsync_RecordsPayloadOffsetsForFiles()
    {
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["alpha.txt"] = Encoding.UTF8.GetBytes("alpha-root"),
            ["empty.bin"] = Array.Empty<byte>(),
            ["nested/beta.txt"] = Encoding.UTF8.GetBytes("nested-beta"),
            ["nested/gamma.txt"] = Encoding.UTF8.GetBytes("gamma-after-beta"),
        };

        await using var pxar = new MemoryStream();
        await PxarEncoder.EncodeAsync(BuildTree(expected), pxar);

        var index = await PxarIndexer.BuildIndexAsync(pxar);
        var byPath = index.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);

        Assert.Equal(PxarEntryKind.Directory, byPath[string.Empty].Kind);
        Assert.Equal(PxarEntryKind.Directory, byPath["nested"].Kind);
        Assert.Equal(PxarEntryKind.Symlink, byPath["nested/link-to-alpha"].Kind);
        Assert.Equal(0, byPath["nested/link-to-alpha"].PayloadOffset);
        Assert.Equal(0, byPath["nested/link-to-alpha"].PayloadLength);

        foreach (var (path, bytes) in expected)
        {
            var entry = byPath[path];
            Assert.Equal(PxarEntryKind.File, entry.Kind);
            Assert.Equal(bytes.Length, entry.PayloadLength);

            var actual = new byte[entry.PayloadLength];
            pxar.Position = entry.PayloadOffset;
            var read = await pxar.ReadAsync(actual);

            Assert.Equal(bytes.Length, read);
            Assert.Equal(bytes, actual);
        }
    }

    [Fact]
    public async Task BuildIndexAsync_AdvancesOffsetsPastPriorPayloads()
    {
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["first.txt"] = Encoding.UTF8.GetBytes("first-file-payload"),
            ["second.txt"] = Encoding.UTF8.GetBytes("second-file-payload"),
        };

        await using var pxar = new MemoryStream();
        await PxarEncoder.EncodeAsync(BuildTree(expected), pxar);

        var index = await PxarIndexer.BuildIndexAsync(pxar);
        var first = Assert.Single(index.Where(entry => entry.RelativePath == "first.txt"));
        var second = Assert.Single(index.Where(entry => entry.RelativePath == "second.txt"));

        Assert.True(second.PayloadOffset > first.PayloadOffset + first.PayloadLength);
        AssertPayload(pxar, first, expected["first.txt"]);
        AssertPayload(pxar, second, expected["second.txt"]);
    }

    private static PxarDirNode BuildTree(IReadOnlyDictionary<string, byte[]> files)
    {
        var root = new PxarDirNode { Name = string.Empty, MtimeSecs = 100 };
        var nested = new PxarDirNode { Name = "nested", MtimeSecs = 200 };

        foreach (var (path, bytes) in files)
        {
            var node = new PxarFileNode
            {
                Name = Path.GetFileName(path),
                Length = bytes.Length,
                MtimeSecs = 300 + bytes.Length,
                OpenRead = () => new MemoryStream(bytes, writable: false),
            };

            if (path.StartsWith("nested/", StringComparison.Ordinal))
                nested.Children.Add(node);
            else
                root.Children.Add(node);
        }

        nested.Children.Add(new PxarSymlinkNode { Name = "link-to-alpha", Target = "../alpha.txt", MtimeSecs = 400 });
        root.Children.Add(nested);
        return root;
    }

    private static void AssertPayload(Stream pxar, PxarEntryLocation entry, byte[] expected)
    {
        var actual = new byte[entry.PayloadLength];
        pxar.Position = entry.PayloadOffset;
        var read = pxar.Read(actual);

        Assert.Equal(expected.Length, read);
        Assert.Equal(expected, actual);
    }
}
