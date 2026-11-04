using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using WatchTower.Configuration;

namespace WatchTower.Portal.Clients;

/// <summary>
/// Shared TLS policy for every portal transport (HTTPS and gRPC): enforces
/// <see cref="TlsOptions.RequireTls"/>, loads the optional mutual-TLS client certificate
/// (<see cref="TlsOptions.ClientCertThumbprint"/>) from <c>LocalMachine\My</c> and builds the
/// primary HTTP handler carrying it.
/// </summary>
/// <remarks>
/// The HTTPS transport uses the named <see cref="IHttpClientFactory"/> client
/// <c>"PortalClient"</c>. To present the client certificate there, attach the handler at
/// registration time:
/// <code>
/// services.AddHttpClient("PortalClient", ...)
///     .ConfigurePrimaryHttpMessageHandler(sp =&gt; PortalTls.CreateHandler(
///         sp.GetRequiredService&lt;IOptionsMonitor&lt;PortalOptions&gt;&gt;().CurrentValue.Tls))
///     .AddHttpMessageHandler&lt;AgentAuthHandler&gt;();
/// </code>
/// Do not pass <c>allowUntrustedServerCert</c> for HTTPS: that transport deliberately fails
/// closed on server-certificate errors.
/// </remarks>
public static class PortalTls
{
    /// <summary>
    /// Throws when <see cref="TlsOptions.RequireTls"/> is set but the configured BaseUrl is not
    /// an <c>https://</c> address, so the agent never sends its token or accepts remote commands
    /// over plaintext by accident.
    /// </summary>
    public static void EnsureTransportAllowed(PortalOptions opts)
    {
        if (opts.Tls.RequireTls &&
            !string.IsNullOrWhiteSpace(opts.BaseUrl) &&
            !opts.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"VisibilityPortal.BaseUrl must use HTTPS when Tls.RequireTls=true. " +
                $"Current BaseUrl: '{opts.BaseUrl}'. " +
                $"Either change the BaseUrl to start with https:// or set Tls.RequireTls=false " +
                $"(for local/HTTP-only deployments).");
        }
    }

    /// <summary>
    /// Loads the client certificate identified by <see cref="TlsOptions.ClientCertThumbprint"/>
    /// from <c>LocalMachine\My</c>. Returns <c>null</c> when no thumbprint is configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The thumbprint is malformed, no matching certificate exists, or it has no private key.
    /// </exception>
    public static X509Certificate2? LoadClientCertificate(TlsOptions tls) =>
        LoadClientCertificate(tls, StoreLocation.LocalMachine);

    internal static X509Certificate2? LoadClientCertificate(TlsOptions tls, StoreLocation location)
    {
        if (string.IsNullOrWhiteSpace(tls.ClientCertThumbprint))
            return null;

        var thumbprint = NormalizeThumbprint(tls.ClientCertThumbprint);
        if (thumbprint.Length == 0 || !thumbprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"VisibilityPortal.Tls.ClientCertThumbprint '{tls.ClientCertThumbprint}' is not a valid " +
                "hexadecimal certificate thumbprint.");
        }

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        // validOnly: false — internal PKI chains are frequently not trusted by the agent
        // itself; the portal is the party that validates the client certificate.
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        try
        {
            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"VisibilityPortal.Tls.ClientCertThumbprint is set to '{thumbprint}' but no such " +
                    $"certificate was found in the {location}\\My certificate store. Import the client " +
                    "certificate (with its private key) or clear the setting.");
            }

            var cert = matches[0];
            if (!cert.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    $"The portal client certificate '{thumbprint}' in {location}\\My has no " +
                    "accessible private key, so it cannot be used for mutual TLS. Re-import it with " +
                    "its private key and grant the service account read access to the key.");
            }

            // Hand ownership of the chosen certificate to the caller; dispose the rest.
            matches.RemoveAt(0);
            return cert;
        }
        finally
        {
            foreach (var other in matches)
                other.Dispose();
        }
    }

    /// <summary>
    /// Creates the primary handler for a portal connection: never follows redirects, presents the
    /// configured client certificate (if any) and, only when
    /// <paramref name="allowUntrustedServerCert"/> is true, skips server-certificate validation
    /// (development use only; callers must guard it).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(TlsOptions tls, bool allowUntrustedServerCert = false)
    {
        var handler = new SocketsHttpHandler
        {
            // gRPC multiplexes calls over HTTP/2; let it open extra connections when the
            // stream limit is reached instead of queueing behind long calls.
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            // Never follow portal redirects: a followed 301/302 turns a POST into a GET whose 2xx
            // would count as delivered, and the request (with its bearer token) could end up at
            // another host. A 3xx is a failure the clients report instead (see HttpsPortalClient).
            // gRPC does not redirect, so this changes nothing for that transport.
            AllowAutoRedirect = false,
        };

        var clientCert = LoadClientCertificate(tls);
        if (clientCert != null)
        {
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCert };
        }

        if (allowUntrustedServerCert)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;
        }

        return handler;
    }

    /// <summary>
    /// Thumbprints copied from the certificate MMC snap-in often contain spaces and an invisible
    /// left-to-right mark; strip everything that is not a letter or digit and upper-case it.
    /// </summary>
    internal static string NormalizeThumbprint(string thumbprint) =>
        new string(thumbprint.Where(char.IsLetterOrDigit).ToArray())
            .ToUpper(CultureInfo.InvariantCulture);
}