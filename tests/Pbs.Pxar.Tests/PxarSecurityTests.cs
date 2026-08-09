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
}
