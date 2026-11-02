using System.Text.Json;
using Pbs.Abstractions;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

/// <summary>Previous-manifest checks before metadata-mode reuse (issue #33 review).</summary>
public class ManifestVerifierTests
{
    private static readonly CryptConfig Key = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly CryptConfig OtherKey = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private static readonly DynamicIndexEntry[] Entries =
    {
        new(1000, ChunkDigest.Compute(new byte[] { 1 })),
        new(2500, ChunkDigest.Compute(new byte[] { 2 })),
    };

    private static string Csum(IReadOnlyList<DynamicIndexEntry> entries)
    {
        long start = 0;
        var refs = entries.Select(e => { var r = new ChunkRef(start, (int)(e.EndOffset - start), e.Digest); start = e.EndOffset; return r; }).ToList();
        return DynamicIndex.ComputeCsum(refs).ToString();
    }

    // A manifest shaped like the ones BackupSession.FinishAsync and upstream write.
    private static JsonElement Manifest(string metaMode = "encrypt", string payloadMode = "encrypt",
        CryptConfig? signWith = null, string? fingerprint = null, Action<Dictionary<string, object?>>? tamper = null)
    {
        var files = new List<Dictionary<string, object?>>
        {
            new() { ["filename"] = "root.mpxar.didx", ["crypt-mode"] = metaMode, ["size"] = 2500L, ["csum"] = Csum(Entries) },
            new() { ["filename"] = "root.ppxar.didx", ["crypt-mode"] = payloadMode, ["size"] = 2500L, ["csum"] = Csum(Entries) },
            new() { ["filename"] = "index.json.blob", ["crypt-mode"] = "none", ["size"] = 1L, ["csum"] = "00" },
        };
        var m = new Dictionary<string, object?>
        {
            ["backup-type"] = "host", ["backup-id"] = "x", ["backup-time"] = 100L, ["files"] = files,
        };
        if (signWith is not null)
            m["signature"] = Convert.ToHexString(signWith.ComputeAuthTag(CanonicalJson.Serialize(m))).ToLowerInvariant();
        m["unprotected"] = fingerprint is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["key-fingerprint"] = fingerprint };
        tamper?.Invoke(m);
        return JsonDocument.Parse(JsonSerializer.Serialize(m)).RootElement;
    }

    [Fact]
    public void SignedManifest_UnderSameKey_IsReusable() =>
        Assert.Null(ManifestVerifier.CheckReusable(Manifest(signWith: Key, fingerprint: Key.FingerprintHex()), Key));

    [Fact]
    public void TamperedFilesList_FailsSignature()
    {
        var m = Manifest(signWith: Key, tamper: d => ((List<Dictionary<string, object?>>)d["files"]!)[1]["csum"] = new string('f', 64));
        Assert.Contains("signature", ManifestVerifier.CheckReusable(m, Key));
    }

    [Fact]
    public void UnsignedManifest_WithKey_IsRejected() =>
        Assert.Contains("not signed", ManifestVerifier.CheckReusable(Manifest(), Key));

    [Fact]
    public void SignatureFromAnotherKey_IsRejected() =>
        Assert.NotNull(ManifestVerifier.CheckReusable(Manifest(signWith: OtherKey), Key));

    [Fact]
    public void DifferentKeyFingerprint_IsRejected() =>
        Assert.Contains("different key", ManifestVerifier.CheckReusable(Manifest(signWith: Key, fingerprint: OtherKey.FingerprintHex()), Key));

    [Fact]
    public void PayloadArchiveInOtherCryptMode_IsRejected()
    {
        // .mpxar matches, .ppxar does not: its chunks must not be injected into an encrypted archive
        Assert.Contains("root.ppxar.didx", ManifestVerifier.CheckReusable(Manifest(payloadMode: "none", signWith: Key), Key));
        Assert.Contains("root.ppxar.didx", ManifestVerifier.CheckReusable(Manifest(metaMode: "none", payloadMode: "encrypt"), null));
    }

    [Fact]
    public void Unencrypted_PlainOrSignOnly_IsReusable() =>
        Assert.Null(ManifestVerifier.CheckReusable(Manifest(metaMode: "none", payloadMode: "sign-only"), null));

    [Fact]
    public void Index_MatchingManifest_Verifies() =>
        ManifestVerifier.VerifyDynamicIndex(Manifest(), "root.ppxar.didx", Entries);

    [Fact]
    public void Index_WithSubstitutedChunk_IsRejected()
    {
        var swapped = new[] { Entries[0], new DynamicIndexEntry(2500, ChunkDigest.Compute(new byte[] { 3 })) };
        var ex = Assert.Throws<InvalidDataException>(() => ManifestVerifier.VerifyDynamicIndex(Manifest(), "root.ppxar.didx", swapped));
        Assert.Contains("checksum", ex.Message);
    }

    [Fact]
    public void Index_WithWrongSize_IsRejected()
    {
        var shorter = new[] { Entries[0] };
        var ex = Assert.Throws<InvalidDataException>(() => ManifestVerifier.VerifyDynamicIndex(Manifest(), "root.ppxar.didx", shorter));
        Assert.Contains("size", ex.Message);
    }

    [Fact]
    public void Index_NotInManifest_IsRejected() =>
        Assert.Throws<InvalidDataException>(() => ManifestVerifier.VerifyDynamicIndex(Manifest(), "other.didx", Entries));
}
