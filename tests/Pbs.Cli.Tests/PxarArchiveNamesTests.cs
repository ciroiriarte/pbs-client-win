using Pbs.Cli;
using Xunit;

namespace Pbs.Cli.Tests;

public class PxarArchiveNamesTests
{
    private static readonly string[] Unified = { "index.json.blob", "root.pxar.didx", "catalog.pcat1.didx" };
    private static readonly string[] Split = { "index.json.blob", "root.mpxar.didx", "root.ppxar.didx" };

    [Theory]
    [InlineData("root.pxar")]
    [InlineData("root.pxar.didx")]
    [InlineData("root")]
    public void Unified_ResolvesToSingleArchive(string name) =>
        Assert.Equal(("root.pxar.didx", (string?)null), PxarArchiveNames.Resolve(name, Unified));

    [Theory]
    [InlineData("root.pxar")]
    [InlineData("root")]
    [InlineData("root.mpxar")]
    [InlineData("root.mpxar.didx")]
    [InlineData("root.ppxar.didx")]
    public void Split_ResolvesToMetadataAndPayloadPair(string name) =>
        Assert.Equal(("root.mpxar.didx", (string?)"root.ppxar.didx"), PxarArchiveNames.Resolve(name, Split));

    [Fact]
    public void Missing_Throws() =>
        Assert.Throws<ArgumentException>(() => PxarArchiveNames.Resolve("data.pxar", Split));

    [Fact]
    public void SplitWithoutPayload_Throws() =>
        Assert.Throws<InvalidDataException>(() => PxarArchiveNames.Resolve("root.pxar", new[] { "root.mpxar.didx" }));

    [Theory]
    [InlineData("root.pxar", "root")]
    [InlineData("root.mpxar.didx", "root")]
    [InlineData("root", "root")]
    [InlineData("data.ppxar", "data")]
    public void BaseName_StripsArchiveExtensions(string name, string expected) =>
        Assert.Equal(expected, PxarArchiveNames.BaseName(name));
}
