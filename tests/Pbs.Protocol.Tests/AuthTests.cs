using Pbs.Protocol;

namespace Pbs.Protocol.Tests;

public class PbsConnectionOptionsAuthTests
{
    [Fact]
    public void ApplyAuth_UsesTokenAuthorizationHeader()
    {
        var options = new PbsConnectionOptions
        {
            BaseUrl = new Uri("https://pbs.test:8007/"),
            Token = new PbsApiToken("backup@pbs!win", "secret"),
        };
        using var http = new HttpClient();

        options.ApplyAuth(http.DefaultRequestHeaders);

        Assert.Equal("PBSAPIToken", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("backup@pbs!win:secret", http.DefaultRequestHeaders.Authorization.Parameter);
        Assert.False(http.DefaultRequestHeaders.Contains("Cookie"));
        Assert.False(http.DefaultRequestHeaders.Contains("CSRFPreventionToken"));
    }

    [Fact]
    public void AuthHeaderLines_UsesByteCompatibleTokenLine()
    {
        var options = new PbsConnectionOptions
        {
            BaseUrl = new Uri("https://pbs.test:8007/"),
            Token = new PbsApiToken("backup@pbs!win", "secret"),
        };

        Assert.Equal(
            new[] { "Authorization: PBSAPIToken backup@pbs!win:secret\r\n" },
            options.AuthHeaderLines().ToArray());
    }

    [Fact]
    public void ApplyAuth_UsesTicketCookieAndCsrfHeaders()
    {
        var ticket = new PbsTicket("ticket-value", "csrf-value", "root@pam", DateTimeOffset.UtcNow);
        var options = new PbsConnectionOptions
        {
            BaseUrl = new Uri("https://pbs.test:8007/"),
            Ticket = ticket,
        };
        using var http = new HttpClient();

        options.ApplyAuth(http.DefaultRequestHeaders);

        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal("PBSAuthCookie=ticket-value", Assert.Single(http.DefaultRequestHeaders.GetValues("Cookie")));
        Assert.Equal("csrf-value", Assert.Single(http.DefaultRequestHeaders.GetValues("CSRFPreventionToken")));
    }

    [Fact]
    public void AuthHeaderLines_UsesTicketCookieAndCsrfLines()
    {
        var ticket = new PbsTicket("ticket-value", "csrf-value", "root@pam", DateTimeOffset.UtcNow);
        var options = new PbsConnectionOptions
        {
            BaseUrl = new Uri("https://pbs.test:8007/"),
            Ticket = ticket,
        };

        Assert.Equal(
            new[]
            {
                "Cookie: PBSAuthCookie=ticket-value\r\n",
                "CSRFPreventionToken: csrf-value\r\n",
            },
            options.AuthHeaderLines().ToArray());
    }
}

public class PbsTicketTests
{
    [Fact]
    public void IsLikelyValid_IsTrueInsideTwoHourWindow()
    {
        var ticket = new PbsTicket("ticket", "csrf", "root@pam", DateTimeOffset.UtcNow.AddMinutes(-119));

        Assert.True(ticket.IsLikelyValid);
    }

    [Fact]
    public void IsLikelyValid_IsFalseAfterTwoHourWindow()
    {
        var ticket = new PbsTicket("ticket", "csrf", "root@pam", DateTimeOffset.UtcNow.AddHours(-2).AddSeconds(-1));

        Assert.False(ticket.IsLikelyValid);
    }
}

public class TicketStoreTests
{
    [Fact]
    public void SaveLoadDelete_RoundTripsInProfileDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pbs-ticket-tests", Guid.NewGuid().ToString("N"));
        var store = new TicketStore(dir);
        var ticket = new PbsTicket("ticket-value", "csrf-value", "root@pam", DateTimeOffset.UtcNow);

        try
        {
            store.Save("pbs.test:8007", ticket);

            var loaded = store.TryLoad("pbs.test:8007", "root@pam");
            Assert.Equal(ticket, loaded);

            store.Delete("pbs.test:8007", "root@pam");
            Assert.Null(store.TryLoad("pbs.test:8007", "root@pam"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
