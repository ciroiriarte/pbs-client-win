using Pbs.Cli;
using Xunit;

namespace Pbs.Cli.Tests;

public class PbsRepositoryTests
{
    [Fact]
    public void DatastoreOnly_DefaultsToLocalhost()
    {
        var r = PbsRepository.Parse("backup01");
        Assert.Null(r.AuthId);
        Assert.Equal("localhost", r.Host);
        Assert.Equal(8007, r.Port);
        Assert.Equal("backup01", r.Datastore);
    }

    [Fact]
    public void HostAndDatastore()
    {
        var r = PbsRepository.Parse("pbs.example.com:store01");
        Assert.Null(r.AuthId);
        Assert.Equal("pbs.example.com", r.Host);
        Assert.Equal(8007, r.Port);
        Assert.Equal("store01", r.Datastore);
    }

    [Fact]
    public void FullTokenHostPortDatastore()
    {
        var r = PbsRepository.Parse("root@pam!windows-test@pbs-01.lan:8123:backup01");
        Assert.Equal("root@pam!windows-test", r.AuthId);
        Assert.Equal("pbs-01.lan", r.Host);
        Assert.Equal(8123, r.Port);
        Assert.Equal("backup01", r.Datastore);
        Assert.Equal(new Uri("https://pbs-01.lan:8123/"), r.BaseUrl);
    }

    [Fact]
    public void TokenAndHostDefaultPort()
    {
        var r = PbsRepository.Parse("user@pbs!tok@pbs.example:store01");
        Assert.Equal("user@pbs!tok", r.AuthId);
        Assert.Equal("pbs.example", r.Host);
        Assert.Equal(8007, r.Port);
        Assert.Equal("store01", r.Datastore);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:")]
    public void Invalid_Throws(string spec)
    {
        Assert.Throws<ArgumentException>(() => PbsRepository.Parse(spec));
    }
}
