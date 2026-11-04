namespace WatchTower.Models;

/// <summary>Latest result of one endpoint check (snapshot table <c>endpoint_status</c>).</summary>
/// <param name="Type">Http | Tcp | Certificate.</param>
/// <param name="Target">Checked target with any credentials and query string removed.</param>
/// <param name="Status">
/// <c>Up</c>, <c>Down</c> or <c>Expiring</c> (certificate valid but expiring soon), or
/// <see cref="ConfigurationError"/> while the check is configured but invalid.
/// </param>
/// <param name="IsUp">
/// False for <c>Down</c>. For <see cref="ConfigurationError"/> it is the value of the last result.
/// </param>
/// <param name="ResponseTimeMs">Time to answer (HTTP response headers, TCP connect, TLS handshake).</param>
/// <param name="CertificateDaysLeft">Certificate checks: days until the certificate expires.</param>
/// <param name="Detail">Human-readable result, e.g. "HTTP 200" or the failure reason.</param>
public record EndpointStatus(
    DateTime Timestamp,
    string Name,
    string Type,
    string Target,
    string Status,
    bool IsUp,
    double? ResponseTimeMs = null,
    double? CertificateDaysLeft = null,
    string? Detail = null
)
{
    /// <summary>
    /// The check is still configured but its configuration is invalid, so it does not run. The
    /// row keeps the last result (timestamp, type, target, isUp, measurements) with this status
    /// and a <see cref="Detail"/> of the form "Configuration error: &lt;error&gt; Last result: &lt;status&gt;[: &lt;detail&gt;]".
    /// </summary>
    public const string ConfigurationError = "ConfigurationError";

    internal const string ConfigurationErrorPrefix = "Configuration error: ";
    internal const string LastResultSeparator = " Last result: ";
}