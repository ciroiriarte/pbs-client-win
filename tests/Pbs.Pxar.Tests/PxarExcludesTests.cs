using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

public class PxarExcludesTests
{
    [Theory]
    [InlineData("*.tmp", "a/b/x.tmp", "x.tmp", true)]
    [InlineData("*.tmp", "a/b/x.txt", "x.txt", false)]
    [InlineData("node_modules", "src/node_modules", "node_modules", true)]  // name anywhere
    [InlineData("node_modules", "src/other", "other", false)]
    [InlineData("/logs/**", "logs/2026/app.log", "app.log", true)]          // anchored path
    [InlineData("/logs/**", "src/logs/app.log", "app.log", false)]          // not at root
    [InlineData("cache/*", "cache/data", "data", true)]
    [InlineData("cache/*", "cache/sub/data", "data", false)]                // * is one segment
    [InlineData("**/temp", "a/b/temp", "temp", true)]
    public void Matches(string pattern, string relPath, string name, bool expected)
    {
        var ex = new PxarExcludes(new[] { pattern });
        Assert.Equal(expected, ex.IsExcluded(relPath, name));
    }

    [Fact]
    public void Empty_MatchesNothing()
    {
        var ex = new PxarExcludes(Array.Empty<string>());
        Assert.True(ex.IsEmpty);
        Assert.False(ex.IsExcluded("anything", "anything"));
    }

    [Fact]
    public void MultiplePatterns_AnyMatches()
    {
        var ex = new PxarExcludes(new[] { "*.tmp", "*.log" });
        Assert.True(ex.IsExcluded("a/x.tmp", "x.tmp"));
        Assert.True(ex.IsExcluded("a/x.log", "x.log"));
        Assert.False(ex.IsExcluded("a/x.txt", "x.txt"));
    }
}
