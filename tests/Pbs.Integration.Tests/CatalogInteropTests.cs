using Pbs.Protocol;
using Pbs.Pxar;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Parses a catalog produced by the OFFICIAL proxmox-backup-client (snapshot host/official-cat),
/// proving our .pcat1 parser matches upstream and revealing the top-level structure.
/// </summary>
public class CatalogInteropTests
{
    private readonly ITestOutputHelper _out;
    public CatalogInteropTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task ParseOfficialClientCatalog()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        using var client = PbsTestServer.CreateClient();
        var snaps = await client.ListSnapshotsAsync("official-cat");
        Skip.If(snaps.Count == 0, "no host/official-cat snapshot on the server");
        var time = snaps[0].GetProperty("backup-time").GetInt64();

        await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", "official-cat", time);
        using var buf = new MemoryStream();
        await reader.RestoreDynamicArchiveAsync("catalog.pcat1.didx", buf);
        _out.WriteLine($"official catalog bytes: {buf.Length}");

        var root = PxarCatalog.Parse(buf.ToArray());
        foreach (var item in PxarCatalog.Enumerate(root))
            _out.WriteLine($"{(char)item.Type} {item.Path} size={item.Size?.ToString() ?? "-"}");

        Assert.NotEmpty(PxarCatalog.Enumerate(root));
    }
}
