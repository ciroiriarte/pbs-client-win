namespace Pbs.Cli;

/// <summary>
/// A parsed PBS repository spec, in the same shape the official client accepts:
/// <c>[[AUTHID@]HOST[:PORT]:]DATASTORE</c> (e.g. <c>root@pam!tok@pbs.example:backup01</c> or just
/// <c>backup01</c> for localhost). The token secret and fingerprint are supplied separately.
/// </summary>
public sealed record PbsRepository(string? AuthId, string Host, int Port, string Datastore)
{
    public const int DefaultPort = 8007;

    public static PbsRepository Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("empty repository", nameof(spec));

        int lastColon = spec.LastIndexOf(':');
        string datastore = lastColon < 0 ? spec : spec[(lastColon + 1)..];
        string server = lastColon < 0 ? string.Empty : spec[..lastColon];
        if (datastore.Length == 0) throw new ArgumentException($"repository '{spec}' has no datastore", nameof(spec));

        string? authId = null;
        string host = "localhost";
        int port = DefaultPort;

        if (server.Length > 0)
        {
            int lastAt = server.LastIndexOf('@');
            string hostPort = server;
            if (lastAt >= 0)
            {
                authId = server[..lastAt];
                hostPort = server[(lastAt + 1)..];
            }
            int hp = hostPort.LastIndexOf(':');
            if (hp >= 0)
            {
                host = hostPort[..hp];
                if (!int.TryParse(hostPort[(hp + 1)..], out port))
                    throw new ArgumentException($"invalid port in repository '{spec}'", nameof(spec));
            }
            else if (hostPort.Length > 0)
            {
                host = hostPort;
            }
        }

        return new PbsRepository(authId, host, port, datastore);
    }

    public Uri BaseUrl => new($"https://{Host}:{Port}/");
}
