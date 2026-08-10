using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class KeyManagementTests
{
    [Fact]
    public void ChangePassphrase_PreservesRawKeyCreatedAndFingerprint()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "test.key");
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        const string created = "2026-08-09T10:23:00-03:00";

        try
        {
            var fingerprint = KeyFile.Write(path, key, "old-passphrase", created);
            var before = KeyFile.ReadMetadata(path);
            Thread.Sleep(TimeSpan.FromSeconds(1.1));

            var changedFingerprint = KeyFile.ChangePassphrase(path, "old-passphrase", "new-passphrase");
            var after = KeyFile.ReadMetadata(path);

            Assert.Equal(fingerprint, changedFingerprint);
            Assert.Equal(key, KeyFile.LoadRawKey(path, "new-passphrase"));
            Assert.Equal(created, before.Created);
            Assert.Equal(created, after.Created);
            Assert.Equal(fingerprint, after.Fingerprint);
            Assert.NotEqual(before.Modified, after.Modified);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnprotectedKeyfileJson_HasUpstreamKeyfileShape()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        const string created = "2026-08-09T10:23:00-03:00";
        const string modified = "2026-08-09T10:24:00-03:00";

        using var doc = JsonDocument.Parse(KeyFile.UnprotectedKeyfileJson(key, created, modified));
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("kdf").ValueKind);
        Assert.Equal(created, root.GetProperty("created").GetString());
        Assert.Equal(modified, root.GetProperty("modified").GetString());
        Assert.Equal(Convert.ToBase64String(key), root.GetProperty("data").GetString());
        Assert.Equal(new CryptConfig(key).FingerprintHex(), root.GetProperty("fingerprint").GetString());
        Assert.Equal(new[] { "kdf", "created", "modified", "data", "fingerprint" }, root.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void MasterKey_CreateEncryptAndDecrypt_RoundTripsJson()
    {
        var dir = Directory.CreateTempSubdirectory();
        var publicPath = Path.Combine(dir.FullName, "master-public.pem");
        var privatePath = Path.Combine(dir.FullName, "master-private.pem");
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i + 7)).ToArray();
        var json = KeyFile.UnprotectedKeyfileJson(key, "2026-08-09T10:23:00-03:00", "2026-08-09T10:24:00-03:00");

        try
        {
            MasterKey.CreateMasterKey(publicPath, privatePath, passphrase: null);
            var publicPem = File.ReadAllText(publicPath);
            var privatePem = File.ReadAllText(privatePath);

            using var publicRsa = RSA.Create();
            publicRsa.ImportFromPem(publicPem);
            Assert.Equal(4096, publicRsa.KeySize);

            var ciphertext = MasterKey.RsaEncryptKeyfile(publicPem, json);
            Assert.Equal(512, ciphertext.Length);

            using var privateRsa = RSA.Create();
            privateRsa.ImportFromPem(privatePem);
            var plaintext = privateRsa.Decrypt(ciphertext, RSAEncryptionPadding.Pkcs1);
            Assert.Equal(json, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void MasterKey_BlobUsesUncompressedDataBlobMagic()
    {
        var dir = Directory.CreateTempSubdirectory();
        var publicPath = Path.Combine(dir.FullName, "master-public.pem");
        var privatePath = Path.Combine(dir.FullName, "master-private.pem");

        try
        {
            MasterKey.CreateMasterKey(publicPath, privatePath, passphrase: null);
            var blob = MasterKey.BuildRsaEncryptedKeyBlob(
                File.ReadAllText(publicPath),
                KeyFile.UnprotectedKeyfileJson(new byte[32], "2026-08-09T10:23:00-03:00", "2026-08-09T10:24:00-03:00"));

            Assert.Equal(FileFormats.UncompressedBlobMagic, blob.AsSpan(0, 8).ToArray());
            Assert.Equal(512, DataBlob.Decode(blob).Length);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
