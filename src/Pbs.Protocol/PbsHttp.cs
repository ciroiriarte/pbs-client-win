using System.Net;
using System.Net.Security;

namespace Pbs.Protocol;

internal static class PbsHttp
{
    public static SocketsHttpHandler CreateHandler(CertificateFingerprint? fingerprint) =>
        new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions =
            {
                RemoteCertificateValidationCallback = ValidationCallback(fingerprint),
            },
        };

    private static RemoteCertificateValidationCallback ValidationCallback(CertificateFingerprint? pinned) =>
        (_, cert, _, errors) =>
            pinned is null ? errors == SslPolicyErrors.None
                           : cert is not null && pinned.Matches(cert);
}
