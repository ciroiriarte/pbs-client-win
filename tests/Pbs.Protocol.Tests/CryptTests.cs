using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class CryptConfigTests
{
    // A real keyfile produced by `proxmox-backup-client key create --kdf none`: this base64 key
    // has exactly this fingerprint per the official client. If our derivation matches upstream,
    // CryptConfig.FingerprintHex() reproduces it.
    private const string RealKeyBase64 = "ty1PYdB2OfWc8IkaGVh/Rtvu2d/CFv7rQ231UVpV07I=";
    private const string RealFingerprint =
        "d2:98:3f:fc:5a:13:f3:04:ab:b7:e1:40:cd:96:94:7f:11:d1:c3:5d:81:c5:9e:0f:56:7f:ab:48:26:b0:a1:67";

    [Fact]
    public void Fingerprint_MatchesOfficialClient()
    {
        var key = Convert.FromBase64String(RealKeyBase64);
        var crypt = new CryptConfig(key);
        Assert.Equal(RealFingerprint, crypt.FingerprintHex());
    }

    [Fact]
    public void EncryptBlob_RoundTrips_Uncompressed()
    {
        var crypt = new CryptConfig(Convert.FromBase64String(RealKeyBase64));
        var data = new byte[4096];
        new Random(1).NextBytes(data); // incompressible

        var blob = crypt.EncryptBlob(data, compress: true);
        Assert.Equal(FileFormats.EncryptedBlobMagic, blob.AsSpan(0, 8).ToArray());
        Assert.Equal(data, crypt.DecryptBlob(blob));
    }

    [Fact]
    public void EncryptBlob_RoundTrips_Compressed()
    {
        var crypt = new CryptConfig(Convert.FromBase64String(RealKeyBase64));
        var data = new byte[20000]; // all zeros -> compresses

        var blob = crypt.EncryptBlob(data, compress: true);
        Assert.Equal(FileFormats.EncrComprBlobMagic, blob.AsSpan(0, 8).ToArray());
        Assert.True(blob.Length < data.Length);
        Assert.Equal(data, crypt.DecryptBlob(blob));
    }

    [Fact]
    public void TamperedCiphertext_FailsCrcOrTag()
    {
        var crypt = new CryptConfig(Convert.FromBase64String(RealKeyBase64));
        var data = new byte[1000];
        new Random(2).NextBytes(data);
        var blob = crypt.EncryptBlob(data, compress: false);
        blob[^1] ^= 0xFF; // corrupt ciphertext

        Assert.ThrowsAny<Exception>(() => crypt.DecryptBlob(blob));
    }

    [Fact]
    public void ComputeDigest_DiffersFromPlainSha256_AndIsStable()
    {
        var crypt = new CryptConfig(Convert.FromBase64String(RealKeyBase64));
        var data = "chunk contents"u8.ToArray();

        var d1 = crypt.ComputeDigest(data);
        var d2 = crypt.ComputeDigest(data);
        Assert.Equal(d1, d2);                                   // deterministic
        Assert.NotEqual(System.Security.Cryptography.SHA256.HashData(data), d1); // keyed, not plain sha256
    }

    [Fact]
    public void Invalid_KeyLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CryptConfig(new byte[16]));
    }
}
