using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pbs.Protocol;

/// <summary>
/// A pinned SHA-256 certificate fingerprint. PBS deployments typically use a self-signed server
/// certificate, so clients pin its fingerprint (as shown in the PBS UI, e.g.
/// <c>AA:BB:CC:…</c>) instead of relying on a public CA chain.
/// </summary>
public sealed class CertificateFingerprint : IEquatable<CertificateFingerprint>
{
    /// <summary>Normalized fingerprint: 64 uppercase hex chars, no separators.</summary>
    public string Value { get; }

    private CertificateFingerprint(string normalized) => Value = normalized;

    /// <summary>Parse a fingerprint written with or without <c>:</c>/whitespace separators.</summary>
    public static CertificateFingerprint Parse(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var hex = fingerprint.Replace(":", "").Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
        if (hex.Length != 64 || !IsHex(hex))
            throw new ArgumentException("expected a 32-byte (64 hex char) SHA-256 fingerprint", nameof(fingerprint));
        return new CertificateFingerprint(hex);
    }

    /// <summary>The SHA-256 fingerprint of an actual certificate (DER-encoded).</summary>
    public static CertificateFingerprint OfCertificate(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var hash = SHA256.HashData(certificate.GetRawCertData());
        return new CertificateFingerprint(Convert.ToHexString(hash));
    }

    /// <summary>True if <paramref name="certificate"/> matches this pinned fingerprint.</summary>
    public bool Matches(X509Certificate certificate) => Equals(OfCertificate(certificate));

    /// <summary>Colon-separated display form, as the PBS UI shows it.</summary>
    public string ToDisplayString() => string.Join(':', Enumerable.Range(0, 32).Select(i => Value.Substring(i * 2, 2)));

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    public bool Equals(CertificateFingerprint? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => Equals(obj as CertificateFingerprint);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => ToDisplayString();
}
