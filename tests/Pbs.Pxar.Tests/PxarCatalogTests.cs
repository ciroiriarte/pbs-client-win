using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

public class PxarCatalogTests
{
    [Fact]
    public void BuildParse_RoundTrips()
    {
        var root = new PxarDirNode { Name = "" };
        var archive = new PxarDirNode { Name = "root.pxar.didx", MtimeSecs = 1_700_000_000 };
        archive.Children.Add(new PxarFileNode { Name = "readme.txt", MtimeSecs = 111, Length = 6, OpenRead = () => new MemoryStream() });
        var sub = new PxarDirNode { Name = "docs", MtimeSecs = 222 };
        sub.Children.Add(new PxarFileNode { Name = "data.bin", MtimeSecs = 333, Length = 2000, OpenRead = () => new MemoryStream() });
        sub.Children.Add(new PxarSymlinkNode { Name = "link", Target = "readme.txt", MtimeSecs = 444 });
        archive.Children.Add(sub);
        root.Children.Add(archive);

        var bytes = PxarCatalog.Build(root);
        var parsed = PxarCatalog.Parse(bytes);
        var items = PxarCatalog.Enumerate(parsed).ToDictionary(i => i.Path, i => i);

        Assert.Contains("/root.pxar.didx", items.Keys);
        Assert.Contains("/root.pxar.didx/docs", items.Keys);
        Assert.Equal(6, items["/root.pxar.didx/readme.txt"].Size);
        Assert.Equal(2000, items["/root.pxar.didx/docs/data.bin"].Size);
        Assert.Equal(PxarCatalogEntryType.Symlink, items["/root.pxar.didx/docs/link"].Type);
    }

    [Fact]
    public void Parse_RejectsBadMagic()
    {
        var bad = new byte[32];
        Assert.Throws<InvalidDataException>(() => PxarCatalog.Parse(bad));
    }
}
