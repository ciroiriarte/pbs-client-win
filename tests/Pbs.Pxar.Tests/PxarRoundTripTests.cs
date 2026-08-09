using System.Security.Cryptography;
using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

public class PxarRoundTripTests
{
    [Fact]
    public async Task EncodeThenDecode_ReproducesTree()
    {
        var src = Directory.CreateTempSubdirectory("pxar-enc-").FullName;
        var dst = Directory.CreateTempSubdirectory("pxar-dec-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello world\n");
            File.WriteAllBytes(Path.Combine(src, "empty.txt"), Array.Empty<byte>());
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            var big = new byte[50_000];
            new Random(9).NextBytes(big);
            File.WriteAllBytes(Path.Combine(src, "sub", "data.bin"), big);
            File.WriteAllText(Path.Combine(src, "sub", "note.md"), "# note\n");
            Directory.CreateDirectory(Path.Combine(src, "sub", "nested"));
            File.WriteAllText(Path.Combine(src, "sub", "nested", "deep.txt"), "deep\n");

            using var archive = new MemoryStream();
            await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(src), archive);
            archive.Position = 0;
            await PxarDecoder.ExtractAsync(archive, dst);

            var srcFiles = HashTree(src);
            var dstFiles = HashTree(dst);
            Assert.Equal(srcFiles, dstFiles);
        }
        finally
        {
            Directory.Delete(src, recursive: true);
            Directory.Delete(dst, recursive: true);
        }
    }

    private static SortedDictionary<string, string> HashTree(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            result[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        }
        return result;
    }
}
