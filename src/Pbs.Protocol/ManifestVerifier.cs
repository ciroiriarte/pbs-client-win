using System.Text.Json;
using Pbs.Abstractions;

namespace Pbs.Protocol;

/// <summary>
/// Checks on a previous snapshot's manifest before its archives are reused (issue #33 review),
/// following upstream <c>pbs-datastore/src/manifest.rs</c> and <c>check_previous_manifest</c>:
/// the HMAC signature when a key is in use, the chunk encryption mode of every archive, and each
/// reused index against the checksum and size recorded in the manifest.
/// </summary>
public static class ManifestVerifier
{
    /// <summary>
    /// Why the previous manifest must not be reused by a backup in this crypt mode, or null if it may.
    /// With a key, the manifest must carry a valid signature under that key: an unsigned or forged
    /// manifest would let the server substitute other chunks encrypted under the same key.
    /// </summary>
    public static string? CheckReusable(JsonElement manifest, CryptConfig? crypt)
    {
        bool encrypt = crypt is not null;
        foreach (var file in Files(manifest))
        {
            var name = file.GetProperty("filename").GetString() ?? "";
            if (name.EndsWith(".blob", StringComparison.Ordinal)) continue;
            bool fileEncrypted = CryptModeOf(file) == "encrypt"; // sign-only and none use plain chunks
            if (fileEncrypted != encrypt)
                return $"previous archive '{name}' has a different chunk encryption mode";
        }

        if (crypt is null) return null;

        if (manifest.TryGetProperty("unprotected", out var u) && u.ValueKind == JsonValueKind.Object &&
            u.TryGetProperty("key-fingerprint", out var fp) && fp.ValueKind == JsonValueKind.String &&
            !string.Equals(fp.GetString(), crypt.FingerprintHex(), StringComparison.OrdinalIgnoreCase))
            return "previous snapshot was encrypted with a different key";

        if (!manifest.TryGetProperty("signature", out var sig) || sig.ValueKind != JsonValueKind.String)
            return "previous manifest is not signed";
        var expected = Convert.ToHexString(crypt.ComputeAuthTag(CanonicalJson.Serialize(SignedFields(manifest)))).ToLowerInvariant();
        return string.Equals(sig.GetString(), expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : "previous manifest signature does not verify";
    }

    /// <summary>
    /// Throw unless <paramref name="entries"/> (a downloaded dynamic index) matches the checksum and size
    /// the manifest records for <paramref name="fileName"/>. Recomputed from the entries, never taken from
    /// the index header (upstream: "do not use values stored in index").
    /// </summary>
    public static void VerifyDynamicIndex(JsonElement manifest, string fileName, IReadOnlyList<DynamicIndexEntry> entries)
    {
        var file = Files(manifest).FirstOrDefault(f => f.GetProperty("filename").GetString() == fileName);
        if (file.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"manifest does not contain file '{fileName}'");

        var chunks = new List<ChunkRef>(entries.Count);
        long start = 0;
        foreach (var e in entries)
        {
            chunks.Add(new ChunkRef(start, checked((int)(e.EndOffset - start)), e.Digest));
            start = e.EndOffset;
        }
        var size = file.GetProperty("size").GetUInt64();
        if ((ulong)start != size)
            throw new InvalidDataException($"wrong size for file '{fileName}' ({size} != {start})");
        var csum = DynamicIndex.ComputeCsum(chunks).ToString();
        if (!string.Equals(file.GetProperty("csum").GetString(), csum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"wrong checksum for file '{fileName}'");
    }

    // What upstream signs: the manifest struct serialized without "unprotected" and "signature".
    internal static Dictionary<string, object?> SignedFields(JsonElement manifest) => new()
    {
        ["backup-type"] = manifest.GetProperty("backup-type").GetString(),
        ["backup-id"] = manifest.GetProperty("backup-id").GetString(),
        ["backup-time"] = manifest.GetProperty("backup-time").GetInt64(),
        ["files"] = Files(manifest).Select(f => (object?)new Dictionary<string, object?>
        {
            ["filename"] = f.GetProperty("filename").GetString(),
            ["crypt-mode"] = CryptModeOf(f),
            ["size"] = checked((long)f.GetProperty("size").GetUInt64()),
            ["csum"] = f.GetProperty("csum").GetString(),
        }).ToList(),
    };

    private static string CryptModeOf(JsonElement file) =>
        file.TryGetProperty("crypt-mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "none";

    private static IEnumerable<JsonElement> Files(JsonElement manifest) =>
        manifest.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
