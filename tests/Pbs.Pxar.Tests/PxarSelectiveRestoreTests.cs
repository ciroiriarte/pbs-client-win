using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

public class PxarSelectiveRestoreTests
{
    [Fact]
    public async Task ExtractAsync_WithIncludePatterns_ExtractsOnlyMatchingFilesAndConsumesStream()
    {
        var src = Directory.CreateTempSubdirectory("pxar-sel-src-").FullName;
        var dst = Directory.CreateTempSubdirectory("pxar-sel-dst-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(src, "file1.txt"), "hello txt 1\n");
            File.WriteAllText(Path.Combine(src, "file2.log"), "hello log 2\n");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            var binData = new byte[1024];
            new Random(42).NextBytes(binData);
            File.WriteAllBytes(Path.Combine(src, "sub", "data.bin"), binData);
            File.WriteAllText(Path.Combine(src, "sub", "note.md"), "# note\n");
            Directory.CreateDirectory(Path.Combine(src, "sub", "nested"));
            File.WriteAllText(Path.Combine(src, "sub", "nested", "deep.txt"), "deep txt\n");
            File.WriteAllText(Path.Combine(src, "sub", "nested", "deep.log"), "deep log\n");

            using var archive = new MemoryStream();
            await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(src), archive);
            archive.Position = 0;

            var patterns = new[] { "*.txt", "sub/data.bin" };
            await PxarDecoder.ExtractAsync(archive, dst, patterns);

            // The whole stream was consumed (non-matching payloads skipped, not left dangling).
            Assert.Equal(archive.Length, archive.Position);

            // Matched files written with exact contents.
            Assert.Equal("hello txt 1\n", File.ReadAllText(Path.Combine(dst, "file1.txt")));
            Assert.Equal(binData, File.ReadAllBytes(Path.Combine(dst, "sub", "data.bin")));
            Assert.Equal("deep txt\n", File.ReadAllText(Path.Combine(dst, "sub", "nested", "deep.txt")));

            // Non-matching files not written.
            Assert.False(File.Exists(Path.Combine(dst, "file2.log")));
            Assert.False(File.Exists(Path.Combine(dst, "sub", "note.md")));
            Assert.False(File.Exists(Path.Combine(dst, "sub", "nested", "deep.log")));
        }
        finally
        {
            Directory.Delete(src, recursive: true);
            Directory.Delete(dst, recursive: true);
        }
    }
}
