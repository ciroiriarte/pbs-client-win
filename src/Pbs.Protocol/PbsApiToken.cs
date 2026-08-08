namespace Pbs.Protocol;

/// <summary>
/// A Proxmox Backup API token. On the wire it authenticates via the header
/// <c>Authorization: PBSAPIToken=USER@REALM!TOKENID:SECRET</c>.
/// </summary>
public sealed class PbsApiToken
{
    /// <summary>The token id, e.g. <c>backup@pbs!windows</c> (<c>user@realm!tokenname</c>).</summary>
    public string TokenId { get; }

    /// <summary>The token secret (a UUID issued by PBS).</summary>
    public string Secret { get; }

    public PbsApiToken(string tokenId, string secret)
    {
        if (string.IsNullOrWhiteSpace(tokenId)) throw new ArgumentException("token id required", nameof(tokenId));
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("secret required", nameof(secret));
        if (!tokenId.Contains('!') || !tokenId.Contains('@'))
            throw new ArgumentException("token id must have the form user@realm!tokenname", nameof(tokenId));
        TokenId = tokenId;
        Secret = secret;
    }

    /// <summary>The value for the <c>Authorization</c> header (scheme + parameter).</summary>
    public string ToHeaderValue() => $"PBSAPIToken={TokenId}:{Secret}";
}
