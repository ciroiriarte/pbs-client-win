using System.Security.Cryptography;
using System.Text;

namespace Pbs.Protocol;

public static class MasterKey
{
    public static void CreateMasterKey(string publicPath, string privatePath, string? passphrase)
    {
        using var rsa = RSA.Create(4096);
        File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());
        var privatePem = string.IsNullOrEmpty(passphrase)
            ? rsa.ExportPkcs8PrivateKeyPem()
            : rsa.ExportEncryptedPkcs8PrivateKeyPem(
                passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        File.WriteAllText(privatePath, privatePem);
    }

    public static byte[] RsaEncryptKeyfile(string masterPubKeyPem, string unprotectedKeyfileJson)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(masterPubKeyPem);
        return rsa.Encrypt(Encoding.UTF8.GetBytes(unprotectedKeyfileJson), RSAEncryptionPadding.Pkcs1);
    }

    public static byte[] BuildRsaEncryptedKeyBlob(string masterPubKeyPem, string unprotectedKeyfileJson) =>
        DataBlob.Encode(RsaEncryptKeyfile(masterPubKeyPem, unprotectedKeyfileJson), compress: false);

    public static string ShowMasterPubKey(string pemPath) => File.ReadAllText(pemPath);

    public static (string Fingerprint, int KeySize) ValidatePublicKey(string pemPath)
    {
        var pem = File.ReadAllText(pemPath);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var fp = SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());
        return (string.Join(':', fp.Select(b => b.ToString("x2"))), rsa.KeySize);
    }
}
