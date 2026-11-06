namespace WatchTower.Configuration;

/// <summary>
/// Endpoint checks: <c>Monitoring:Endpoints</c> is a list of <see cref="EndpointCheckOptions"/>.
/// Bound from the <c>Monitoring</c> section (only the <c>Endpoints</c> and <c>EndpointDefaults</c>
/// keys are read). The list is
/// edited in the web console's "Endpoint checks" settings section (or imported with
/// <c>WatchTower config import</c>); the results are shown in the console status.
/// </summary>
public class EndpointMonitorOptions
{
    public const string SectionName = "Monitoring";

    public List<EndpointCheckOptions> Endpoints { get; set; } = [];

    /// <summary>Settings every check uses unless it sets its own: <c>Monitoring:EndpointDefaults</c>.</summary>
    public EndpointDefaultsOptions EndpointDefaults { get; set; } = new();
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

    /// <summary>
    /// Waiting period for this check (seconds): it must have been failing this long, as well as
    /// <see cref="FailuresBeforeAlert"/> times in a row, before the endpoint is reported down.
    /// Null (absent) = <c>Monitoring:EndpointDefaults:SustainedSeconds</c>; 0 = no waiting period.
    /// </summary>
    public int? SustainedSeconds { get; set; }
}

/// <summary>Defaults for every endpoint check: <c>Monitoring:EndpointDefaults</c>.</summary>
public class EndpointDefaultsOptions
{
    public const string SectionName = "Monitoring:EndpointDefaults";

    /// <summary>
    /// Waiting period (seconds) of checks without their own <c>SustainedSeconds</c>: an endpoint
    /// is reported down only once it has been failing this long and
    /// <see cref="EndpointCheckOptions.FailuresBeforeAlert"/> checks in a row. 0 = failures only.
    /// </summary>
    public int SustainedSeconds { get; set; } = 120;
}