using System.Text.Json;

namespace Pbs.Protocol;

/// <summary>
/// Reads a PBS encryption keyfile (as produced by <c>proxmox-backup-client key create</c>). The
/// on-disk JSON is <c>{ "kdf": null, "data": "&lt;base64 32-byte key&gt;", "fingerprint": "aa:bb:…" }</c>.
/// Only unprotected keys (<c>kdf: null</c>) are supported here; password-protected keyfiles
/// (scrypt/pbkdf2) need the passphrase KDF, which is not implemented yet.
/// </summary>
public static class KeyFile
{
    /// <summary>Load the raw 32-byte encryption key from a keyfile on disk.</summary>
    public static byte[] LoadRawKey(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (root.TryGetProperty("kdf", out var kdf) && kdf.ValueKind is not JsonValueKind.Null)
            throw new NotSupportedException($"password-protected keyfiles (kdf '{kdf}') are not supported yet; use 'key create --kdf none'");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("keyfile has no 'data' field");

        var key = Convert.FromBase64String(data.GetString()!);
        if (key.Length != 32) throw new InvalidDataException($"keyfile key is {key.Length} bytes, expected 32");
        return key;
    }
}
