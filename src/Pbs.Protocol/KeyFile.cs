using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Pbs.Protocol;

/// <summary>
/// Reads a PBS encryption keyfile (as produced by <c>proxmox-backup-client key create</c>). The
/// on-disk JSON is <c>{ "kdf": null|{"Scrypt":{n,r,p,salt}}, "data": "&lt;base64&gt;", "fingerprint": … }</c>.
/// For <c>kdf: null</c> the raw 32-byte key is the base64 <c>data</c>. For a scrypt-protected key,
/// <c>data</c> is <c>iv[16] | tag[16] | ciphertext</c>: the key is derived from the passphrase via
/// scrypt and AES-256-GCM-decrypts the raw key.
/// </summary>
public static class KeyFile
{
    private const int TagSize = 16;
    private const int IvSize = 16;

    /// <summary>Load the raw 32-byte encryption key. A passphrase is required for protected keyfiles.</summary>
    public static byte[] LoadRawKey(string path, string? passphrase = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("keyfile has no 'data' field");
        var data = Convert.FromBase64String(dataElement.GetString()!);

        // Unprotected key (kdf: null): data is the raw key.
        if (!root.TryGetProperty("kdf", out var kdf) || kdf.ValueKind == JsonValueKind.Null)
            return Exactly32(data, "keyfile key");

        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("keyfile is password-protected; provide a passphrase (--key-password or PBS_ENCRYPTION_PASSWORD)");

        if (!kdf.TryGetProperty("Scrypt", out var scrypt))
            throw new NotSupportedException("only scrypt-protected keyfiles are supported");

        int n = scrypt.GetProperty("n").GetInt32();
        int r = scrypt.GetProperty("r").GetInt32();
        int p = scrypt.GetProperty("p").GetInt32();
        var salt = Convert.FromBase64String(scrypt.GetProperty("salt").GetString()!);

        var derived = SCrypt.Generate(Encoding.UTF8.GetBytes(passphrase), salt, n, r, p, 32);

        if (data.Length < IvSize + TagSize) throw new InvalidDataException("protected keyfile data too short");
        var iv = data.AsSpan(0, IvSize).ToArray();
        var tag = data.AsSpan(IvSize, TagSize).ToArray();
        var ciphertext = data.AsSpan(IvSize + TagSize).ToArray();

        var key = GcmDecrypt(derived, iv, ciphertext, tag);
        return Exactly32(key, "decrypted keyfile key");
    }

    private static byte[] Exactly32(byte[] key, string what) =>
        key.Length == 32 ? key : throw new InvalidDataException($"{what} is {key.Length} bytes, expected 32");

    private static byte[] GcmDecrypt(byte[] key, byte[] iv, byte[] ciphertext, byte[] tag)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), TagSize * 8, iv, Array.Empty<byte>()));
        var input = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(input, 0);
        tag.CopyTo(input, ciphertext.Length);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);
        return output[..len];
    }
}
