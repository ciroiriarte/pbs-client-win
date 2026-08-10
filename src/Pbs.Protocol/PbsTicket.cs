namespace Pbs.Protocol;

/// <summary>A PBS login ticket and CSRF token pair for cookie authentication.</summary>
public sealed record PbsTicket(string Ticket, string CsrfToken, string Username, DateTimeOffset ObtainedUtc)
{
    /// <summary>PBS tickets are valid for roughly two hours.</summary>
    public bool IsLikelyValid => ObtainedUtc.AddHours(2) > DateTimeOffset.UtcNow;
}
