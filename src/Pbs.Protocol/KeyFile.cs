using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

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

    /// <summary>
    /// Generate a new 32-byte encryption key and write a keyfile in the PBS format (loadable by the
    /// official client). With a <paramref name="passphrase"/> the key is scrypt-protected; without,
    /// it is written unprotected (<c>kdf: null</c>). Returns the key fingerprint.
    /// </summary>
    public static string Create(string path, string? passphrase)
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return Write(path, key, passphrase);
    }

    /// <summary>Write <paramref name="key"/> as a PBS keyfile, preserving <paramref name="created"/> when supplied.</summary>
    public static string Write(string path, byte[] key, string? passphrase, string? created = null)
    {
        key = Exactly32(key, "keyfile key");
        var fingerprint = new CryptConfig(key).FingerprintHex();
        object? kdf;
        string dataB64;
        if (string.IsNullOrEmpty(passphrase))
        {
            kdf = null;
            dataB64 = Convert.ToBase64String(key);
        }
        else
        {
            const int n = 65536, r = 8, p = 1;
            var salt = new byte[32];
            RandomNumberGenerator.Fill(salt);
            var derived = SCrypt.Generate(Encoding.UTF8.GetBytes(passphrase), salt, n, r, p, 32);
            var iv = new byte[IvSize];
            RandomNumberGenerator.Fill(iv);
            var (ciphertext, tag) = GcmEncrypt(derived, iv, key);
            var data = new byte[IvSize + TagSize + ciphertext.Length];
            iv.CopyTo(data, 0);
            tag.CopyTo(data, IvSize);
            ciphertext.CopyTo(data, IvSize + TagSize);
            dataB64 = Convert.ToBase64String(data);
            kdf = new Dictionary<string, object>
            {
                ["Scrypt"] = new Dictionary<string, object> { ["n"] = n, ["r"] = r, ["p"] = p, ["salt"] = Convert.ToBase64String(salt) },
            };
        }

        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        created ??= now;
        var doc = new Dictionary<string, object?>
        {
            ["kdf"] = kdf,
            ["created"] = created,
            ["modified"] = now,
            ["data"] = dataB64,
            ["fingerprint"] = fingerprint,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        return fingerprint;
    }

    /// <summary>Re-encrypt an existing keyfile with <paramref name="newPassphrase"/> without changing the raw key.</summary>
    public static string ChangePassphrase(string path, string? oldPassphrase, string? newPassphrase)
    {
        if (string.IsNullOrEmpty(newPassphrase))
            throw new ArgumentException("new passphrase is required");
        var meta = ReadMetadata(path);
        var key = LoadRawKey(path, oldPassphrase);
        return Write(path, key, newPassphrase, meta.Created);
    }

    /// <summary>JSON form escrowed by a master public key: unprotected 32-byte key and key metadata.</summary>
    public static string UnprotectedKeyfileJson(byte[] key, string? created, string? modified)
    {
        key = Exactly32(key, "keyfile key");
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var doc = new Dictionary<string, object?>
        {
            ["kdf"] = null,
            ["created"] = created ?? now,
            ["modified"] = modified ?? now,
            ["data"] = Convert.ToBase64String(key),
            ["fingerprint"] = new CryptConfig(key).FingerprintHex(),
        };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    /// <summary>Read the non-secret metadata fields from a keyfile.</summary>
    public static KeyFileMetadata ReadMetadata(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new KeyFileMetadata(
            GetString(root, "created"),
            GetString(root, "modified"),
            GetString(root, "fingerprint"));
    }

    private static (byte[] ciphertext, byte[] tag) GcmEncrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagSize * 8, iv, Array.Empty<byte>()));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len);
        int ctLen = output.Length - TagSize;
        return (output[..ctLen], output[ctLen..]);
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

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record KeyFileMetadata(string? Created, string? Modified, string? Fingerprint);
