using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

public class PxarSecurityTests
{
    private static async Task<byte[]> EncodeWithSymlink(string linkName, string target)
    {
        var root = new PxarDirNode { Name = "", MtimeSecs = 1 };
        root.Children.Add(new PxarSymlinkNode { Name = linkName, Target = target, MtimeSecs = 1 });
        using var ms = new MemoryStream();
        await PxarEncoder.EncodeAsync(root, ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("../../etc/passwd")]   // relative escape
    [InlineData("/etc/passwd")]        // absolute
    [InlineData("sub/../../../oops")]  // escape via traversal after descent
    public async Task Extract_RejectsEscapingSymlinkTarget(string target)
    {
        var archive = await EncodeWithSymlink("link", target);
        var dst = Directory.CreateTempSubdirectory("pxar-sec-").FullName;
        try
        {
            using var ms = new MemoryStream(archive);
            await Assert.ThrowsAsync<InvalidDataException>(() => PxarDecoder.ExtractAsync(ms, dst));
        }
        finally { Directory.Delete(dst, recursive: true); }
    }

    [Fact]
    public async Task Extract_AllowsContainedRelativeSymlink()
    {
        var archive = await EncodeWithSymlink("link", "target.txt"); // stays inside root
        var dst = Directory.CreateTempSubdirectory("pxar-sec-ok-").FullName;
        try
        {
            using var ms = new MemoryStream(archive);
            await PxarDecoder.ExtractAsync(ms, dst); // must not throw
        }
        finally { Directory.Delete(dst, recursive: true); }
    }

    private static async Task<byte[]> EncodeDirWithFile(string dirName, string fileName, byte[] content)
    {
        var root = new PxarDirNode { Name = "", MtimeSecs = 1 };
        var sub = new PxarDirNode { Name = dirName, MtimeSecs = 1 };
        sub.Children.Add(new PxarFileNode
        {
            Name = fileName,
            MtimeSecs = 1,
            Length = content.Length,
            OpenRead = () => new MemoryStream(content),
        });
        root.Children.Add(sub);
        using var ms = new MemoryStream();
        await PxarEncoder.EncodeAsync(root, ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> EncodeRootFile(string fileName, byte[] content)
    {
        var root = new PxarDirNode { Name = "", MtimeSecs = 1 };
        root.Children.Add(new PxarFileNode
        {
            Name = fileName,
            MtimeSecs = 1,
            Length = content.Length,
            OpenRead = () => new MemoryStream(content),
        });
        using var ms = new MemoryStream();
        await PxarEncoder.EncodeAsync(root, ms);
        return ms.ToArray();
    }

    // #7: a directory component that is a pre-existing on-disk symlink must not be traversed — a write
    // through it could land outside the extraction root even though the archive's entry names are clean.
    [Fact]
    public async Task Extract_RefusesToWriteThroughPreexistingSymlinkDir()
    {
        var archive = await EncodeDirWithFile("sub", "f.txt", new byte[] { 1, 2, 3 });
        var dst = Directory.CreateTempSubdirectory("pxar-sec-pre-").FullName;
        var outside = Directory.CreateTempSubdirectory("pxar-sec-out-").FullName;
        var link = Path.Combine(dst, "sub");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // symlink creation not permitted here

            using var ms = new MemoryStream(archive);
            await Assert.ThrowsAsync<InvalidDataException>(() => PxarDecoder.ExtractAsync(ms, dst));
            Assert.False(File.Exists(Path.Combine(outside, "f.txt"))); // nothing escaped through the link
        }
        finally
        {
            try { Directory.Delete(link); } catch { /* remove the link, not its target */ }
            try { Directory.Delete(dst, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    // #7: a leaf that already exists as a symlink must not be written through (that would clobber the
    // link's target, potentially outside the root).
    [Fact]
    public async Task Extract_RefusesToOverwritePreexistingSymlinkLeaf()
    {
        var archive = await EncodeRootFile("f.txt", new byte[] { 9, 9, 9 });
        var dst = Directory.CreateTempSubdirectory("pxar-sec-leaf-").FullName;
        var outsideDir = Directory.CreateTempSubdirectory("pxar-sec-leafout-").FullName;
        var outsideFile = Path.Combine(outsideDir, "victim.txt");
        await File.WriteAllTextAsync(outsideFile, "original");
        var link = Path.Combine(dst, "f.txt");
        try
        {
            try { File.CreateSymbolicLink(link, outsideFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

            using var ms = new MemoryStream(archive);
            await Assert.ThrowsAsync<InvalidDataException>(() => PxarDecoder.ExtractAsync(ms, dst));
            Assert.Equal("original", await File.ReadAllTextAsync(outsideFile)); // link target untouched
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(dst, recursive: true); } catch { }
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }
}
