namespace WatchTower.Configuration;

/// <summary>
/// Endpoint checks: <c>Monitoring:Endpoints</c> is a list of <see cref="EndpointCheckOptions"/>.
/// Bound from the <c>Monitoring</c> section (only the <c>Endpoints</c> key is read). The list is
/// edited in the web console's "Endpoint checks" settings section (or imported with
/// <c>WatchTower config import</c>); the results are shown in the console status.
/// </summary>
public class EndpointMonitorOptions
{
    public const string SectionName = "Monitoring";

    public List<EndpointCheckOptions> Endpoints { get; set; } = [];
}

public class EndpointCheckOptions
{
    /// <summary>Unique name; the alert key is <c>endpoint:&lt;Name&gt;</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Http | Tcp | Certificate.</summary>
    public string Type { get; set; } = "Http";

    /// <summary>
    /// Http: an absolute http(s) URL. Tcp: <c>host:port</c>. Certificate: <c>host</c>,
    /// <c>host:port</c> (default 443) or an https URL.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Http: accepted status codes, e.g. <c>200-399</c>, <c>200</c> or <c>200,204,301-302</c>.</summary>
    public string ExpectStatus { get; set; } = "200-399";

    /// <summary>Http: text the response body must contain (ordinal, case-sensitive). Empty = not checked.</summary>
    public string ExpectBodyContains { get; set; } = string.Empty;

    /// <summary>Certificate: warn when the certificate expires in fewer days than this.</summary>
    public int CertificateWarnDays { get; set; } = 21;

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Consecutive failed checks before the endpoint is reported down.</summary>
    public int FailuresBeforeAlert { get; set; } = 2;
}