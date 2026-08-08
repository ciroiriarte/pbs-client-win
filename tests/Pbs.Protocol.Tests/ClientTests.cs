using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class PbsApiTokenTests
{
    [Fact]
    public void HeaderValue_HasExpectedFormat()
    {
        var token = new PbsApiToken("backup@pbs!windows", "00000000-1111-2222-3333-444444444444");
        Assert.Equal("PBSAPIToken=backup@pbs!windows:00000000-1111-2222-3333-444444444444", token.ToHeaderValue());
    }

    [Theory]
    [InlineData("nobang", "secret")]        // missing !
    [InlineData("no-at!tok", "secret")]     // missing @
    [InlineData("user@pbs!tok", "")]        // empty secret
    public void InvalidInputs_Throw(string id, string secret)
    {
        Assert.Throws<ArgumentException>(() => new PbsApiToken(id, secret));
    }
}

public class CertificateFingerprintTests
{
    [Fact]
    public void ColonAndPlainForms_AreEqual()
    {
        var withColons = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
        var plain = withColons.Replace(":", "").ToLowerInvariant();
        Assert.Equal(CertificateFingerprint.Parse(withColons), CertificateFingerprint.Parse(plain));
    }

    [Fact]
    public void BadLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => CertificateFingerprint.Parse("AA:BB:CC"));
    }

    [Fact]
    public void OfCertificate_MatchesPinnedFingerprint()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=pbs.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var expected = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        var pinned = CertificateFingerprint.Parse(expected);

        Assert.True(pinned.Matches(cert));
        Assert.Equal(pinned, CertificateFingerprint.OfCertificate(cert));
    }
}

public class PbsClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task GetVersion_FormsRequestAndParsesResponse()
    {
        var stub = new StubHandler("""{"data":{"version":"3.2.7","release":"1","repoid":"abc123"}}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://pbs.test:8007/") };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("PBSAPIToken", "backup@pbs!win:secret");

        var options = new PbsConnectionOptions
        {
            BaseUrl = new Uri("https://pbs.test:8007/"),
            Token = new PbsApiToken("backup@pbs!win", "secret"),
        };
        using var client = new PbsClient(http, options, ownsHttp: true);

        var version = await client.GetVersionAsync();

        Assert.Equal("3.2.7", version.Version);
        Assert.Equal("1", version.Release);
        Assert.Equal("abc123", version.RepoId);

        Assert.NotNull(stub.LastRequest);
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("https://pbs.test:8007/api2/json/version", stub.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("PBSAPIToken", stub.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("backup@pbs!win:secret", stub.LastRequest.Headers.Authorization.Parameter);
    }
}
