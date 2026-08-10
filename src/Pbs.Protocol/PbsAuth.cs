using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pbs.Protocol;

/// <summary>Login helper for PBS username/password and ticket authentication.</summary>
public static class PbsAuth
{
    public static async Task<PbsTicket> LoginAsync(
        Uri baseUrl,
        CertificateFingerprint? fingerprint,
        string username,
        string password,
        Func<string, string>? tfaCodePrompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username required", nameof(username));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password required", nameof(password));

        using var http = new HttpClient(PbsHttp.CreateHandler(fingerprint))
        {
            BaseAddress = Normalize(baseUrl),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var first = await PostTicketAsync(http, new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
        }, ct);

        if (TryBuildTicket(first, out var ticket))
            return ticket;

        var challenge = ParseTfaChallenge(first);
        if (challenge is null)
            throw new InvalidDataException("login response did not include a usable ticket or 2FA challenge");

        if (challenge.WebAuthnOnly)
            throw new NotSupportedException("WebAuthn 2FA is not supported for headless login; use TOTP or a recovery code");
        if (tfaCodePrompt is null)
            throw new InvalidOperationException("2FA code required but no prompt was provided");

        var code = tfaCodePrompt("2FA code (TOTP or recovery:<code>):").Trim();
        if (code.Length == 0) throw new InvalidOperationException("2FA code required");
        var tfaPassword = code.StartsWith("recovery:", StringComparison.OrdinalIgnoreCase) ? code : $"totp:{code}";

        var second = await PostTicketAsync(http, new Dictionary<string, string>
        {
            ["username"] = username,
            ["tfa-challenge"] = challenge.Token,
            ["password"] = tfaPassword,
        }, ct);

        if (TryBuildTicket(second, out ticket))
            return ticket;

        throw new InvalidDataException("2FA login response did not include a usable ticket");
    }

    private static async Task<LoginData> PostTicketAsync(HttpClient http, Dictionary<string, string> values, CancellationToken ct)
    {
        using var response = await http.PostAsync("api2/json/access/ticket", new FormUrlEncodedContent(values), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"login -> {(int)response.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidDataException("login response missing data");
        return ParseLoginData(data);
    }

    private static LoginData ParseLoginData(JsonElement data)
    {
        var ticket = GetString(data, "ticket");
        var csrf = GetString(data, "CSRFPreventionToken");
        var username = GetString(data, "username");
        JsonElement? tfaChallenge = data.TryGetProperty("tfa-challenge", out var tfa) ? tfa.Clone() : null;
        return new LoginData(ticket, csrf, username, tfaChallenge);
    }

    private static bool TryBuildTicket(LoginData data, out PbsTicket ticket)
    {
        if (!string.IsNullOrWhiteSpace(data.Ticket) &&
            !string.IsNullOrWhiteSpace(data.CsrfToken) &&
            !string.IsNullOrWhiteSpace(data.Username) &&
            !IsTfaChallengeToken(data.Ticket))
        {
            ticket = new PbsTicket(data.Ticket, data.CsrfToken, data.Username, DateTimeOffset.UtcNow);
            return true;
        }

        ticket = null!;
        return false;
    }

    private static TfaChallenge? ParseTfaChallenge(LoginData data)
    {
        string? token = null;
        bool supportsTotp = false;
        bool supportsRecovery = false;
        bool supportsWebAuthn = false;

        if (data.TfaChallenge is { } challenge)
            ParseChallengeElement(challenge, ref token, ref supportsTotp, ref supportsRecovery, ref supportsWebAuthn);

        if (token is null && !string.IsNullOrWhiteSpace(data.Ticket) && IsTfaChallengeToken(data.Ticket))
            token = data.Ticket;

        if (token is null) return null;
        if (!supportsTotp && !supportsRecovery && !supportsWebAuthn)
        {
            supportsTotp = true;
            supportsRecovery = true;
        }

        return new TfaChallenge(token, supportsTotp, supportsRecovery, supportsWebAuthn);
    }

    private static void ParseChallengeElement(
        JsonElement challenge,
        ref string? token,
        ref bool supportsTotp,
        ref bool supportsRecovery,
        ref bool supportsWebAuthn)
    {
        switch (challenge.ValueKind)
        {
            case JsonValueKind.String:
                var value = challenge.GetString();
                if (value is null) return;
                if (value.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    using var parsed = JsonDocument.Parse(value);
                    ParseChallengeElement(parsed.RootElement, ref token, ref supportsTotp, ref supportsRecovery, ref supportsWebAuthn);
                }
                else
                {
                    token = value;
                }
                break;
            case JsonValueKind.Object:
                if (challenge.TryGetProperty("challenge", out var c) && c.ValueKind == JsonValueKind.String) token = c.GetString();
                if (challenge.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.String) token ??= t.GetString();
                foreach (var property in challenge.EnumerateObject())
                {
                    var name = property.Name.ToLowerInvariant();
                    supportsTotp |= name.Contains("totp");
                    supportsRecovery |= name.Contains("recovery");
                    supportsWebAuthn |= name.Contains("webauthn");
                }
                break;
        }
    }

    private static bool IsTfaChallengeToken(string ticket) =>
        ticket.Contains(":tfa!", StringComparison.OrdinalIgnoreCase) ||
        ticket.Contains(":!tfa!", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri Normalize(Uri baseUrl)
    {
        var b = new UriBuilder(baseUrl);
        if (!b.Path.EndsWith('/')) b.Path += "/";
        return b.Uri;
    }

    private sealed record LoginData(
        [property: JsonPropertyName("ticket")] string? Ticket,
        [property: JsonPropertyName("CSRFPreventionToken")] string? CsrfToken,
        [property: JsonPropertyName("username")] string? Username,
        JsonElement? TfaChallenge);

    private sealed record TfaChallenge(string Token, bool SupportsTotp, bool SupportsRecovery, bool SupportsWebAuthn)
    {
        public bool WebAuthnOnly => SupportsWebAuthn && !SupportsTotp && !SupportsRecovery;
    }
}
