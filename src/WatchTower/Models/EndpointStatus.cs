namespace WatchTower.Models;

/// <summary>Latest result of one endpoint check (snapshot table <c>endpoint_status</c>).</summary>
/// <param name="Type">Http | Tcp | Certificate.</param>
/// <param name="Target">Checked target with any credentials and query string removed.</param>
/// <param name="Status"><c>Up</c>, <c>Down</c> or <c>Expiring</c> (certificate valid but expiring soon).</param>
/// <param name="IsUp">False only for <c>Down</c>.</param>
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
);