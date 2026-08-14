using System.Text;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

/// <summary>
/// Pins the manifest-signing primitives (#6) against an INDEPENDENT reference computed with Python's
/// hashlib/hmac (PBKDF2-HMAC-SHA256 id_key + HMAC-SHA256 auth tag) over the canonical manifest JSON —
/// so a regression in canonicalization or the HMAC is caught without a live server. Correctness against
/// the real PBS format is additionally proven by the interop test (the official client verifies the
/// signature on restore).
/// </summary>
public class ManifestSigningTests
{
    private static object BuildProtectedManifest() => new Dictionary<string, object?>
    {
        ["backup-type"] = "host",
        ["backup-id"] = "x",
        ["backup-time"] = 100L,
        ["files"] = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["filename"] = "root.pxar.didx",
                ["crypt-mode"] = "encrypt",
                ["size"] = 5L,
                ["csum"] = "ab",
            },
        },
    };

    [Fact]
    public void CanonicalJson_SortsKeysCompactly()
    {
        var bytes = CanonicalJson.Serialize(BuildProtectedManifest());
        Assert.Equal(
            "{\"backup-id\":\"x\",\"backup-time\":100,\"backup-type\":\"host\"," +
            "\"files\":[{\"crypt-mode\":\"encrypt\",\"csum\":\"ab\",\"filename\":\"root.pxar.didx\",\"size\":5}]}",
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void CanonicalJson_EscapesPerSerdeJson()
    {
        var bytes = CanonicalJson.Serialize(new Dictionary<string, object?> { ["k"] = "a\"b\\c\n\té" });
        // quote/backslash/newline/tab escaped; U+0001 -> ; non-ASCII 'é' emitted raw (UTF-8).
        Assert.Equal("{\"k\":\"a\\\"b\\\\c\\n\\t\\u0001é\"}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ComputeAuthTag_MatchesIndependentReference()
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)i;
        var crypt = new CryptConfig(key);

        var canonical = CanonicalJson.Serialize(BuildProtectedManifest());
        var hex = Convert.ToHexString(crypt.ComputeAuthTag(canonical)).ToLowerInvariant();

        // Reference from Python: HMAC-SHA256(pbkdf2_hmac('sha256', key, b'_id_key', 10, 32), canonical).
        Assert.Equal("c8eb2863faf33987d7a7102964377d27c97753516267d18ca8293aeb227703a5", hex);
    }
}
