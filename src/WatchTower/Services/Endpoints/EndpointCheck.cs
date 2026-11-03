using System.Globalization;

using WatchTower.Configuration;

namespace WatchTower.Services.Endpoints;

public enum EndpointCheckType
{
    Http,
    Tcp,
    Certificate,
}

/// <summary>A validated <see cref="EndpointCheckOptions"/> entry.</summary>
/// <param name="Host">Tcp/Certificate: host name or address to connect to.</param>
/// <param name="Port">Tcp/Certificate: port.</param>
/// <param name="Url">Http: the URL to GET.</param>
/// <param name="DisplayTarget">Target without credentials or query string (safe to show and send).</param>
public sealed record EndpointCheck(
    string Name,
    EndpointCheckType Type,
    string DisplayTarget,
    Uri? Url,
    string? Host,
    int Port,
    TimeSpan Timeout,
    IReadOnlyList<(int From, int To)> ExpectStatus,
    string? ExpectBodyContains,
    int CertificateWarnDays,
    TimeSpan Interval,
    int FailuresBeforeAlert)
{
    public const int MaxNameLength = 100;
    public const int MinIntervalSeconds = 5;
    public const int MaxTimeoutSeconds = 120;

    public bool IsExpectedStatus(int status) => ExpectStatus.Any(r => status >= r.From && status <= r.To);

    /// <summary>Validates and normalises one configured entry. Errors are prefixed with <paramref name="key"/>.</summary>
    public static bool TryCreate(EndpointCheckOptions o, string key, out EndpointCheck check, out string error)
    {
        check = null!;
        error = string.Empty;

        var name = o.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxNameLength)
            return Fail($"{key}:Name is required (at most {MaxNameLength} characters).", out error);
        if (!Enum.TryParse<EndpointCheckType>(o.Type?.Trim(), ignoreCase: true, out var type) || !Enum.IsDefined(type)
            || int.TryParse(o.Type, out _))
            return Fail($"{key}:Type must be Http, Tcp or Certificate.", out error);
        if (o.TimeoutSeconds < 1 || o.TimeoutSeconds > MaxTimeoutSeconds)
            return Fail($"{key}:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.", out error);
        if (o.IntervalSeconds < MinIntervalSeconds || o.IntervalSeconds > 86_400)
            return Fail($"{key}:IntervalSeconds must be between {MinIntervalSeconds} and 86400.", out error);
        if (o.FailuresBeforeAlert < 1 || o.FailuresBeforeAlert > 100)
            return Fail($"{key}:FailuresBeforeAlert must be between 1 and 100.", out error);
        if (o.CertificateWarnDays < 1 || o.CertificateWarnDays > 3650)
            return Fail($"{key}:CertificateWarnDays must be between 1 and 3650.", out error);

        var target = o.Target?.Trim() ?? string.Empty;
        Uri? url = null;
        string? host = null;
        var port = 0;
        string display;

        switch (type)
        {
            case EndpointCheckType.Http:
                if (!Uri.TryCreate(target, UriKind.Absolute, out url) ||
                    (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                    return Fail($"{key}:Target must be an absolute http(s) URL.", out error);
                display = Display(url);
                break;

            case EndpointCheckType.Tcp:
                if (!TrySplitHostPort(target, defaultPort: null, out host, out port))
                    return Fail($"{key}:Target must be host:port.", out error);
                display = $"{FormatHost(host)}:{port}";
                break;

            default:
                if (Uri.TryCreate(target, UriKind.Absolute, out var certUrl) && certUrl.Scheme == Uri.UriSchemeHttps)
                {
                    host = certUrl.IdnHost;
                    port = certUrl.Port;
                }
                else if (!TrySplitHostPort(target, defaultPort: 443, out host, out port))
                {
                    return Fail($"{key}:Target must be host, host:port or an https URL.", out error);
                }
                display = $"{FormatHost(host!)}:{port}";
                break;
        }

        if (!TryParseStatusRanges(o.ExpectStatus, out var ranges))
            return Fail($"{key}:ExpectStatus must be status codes or ranges between 100 and 599, e.g. 200-399 or 200,204.", out error);

        check = new EndpointCheck(
            name, type, display, url, host, port,
            TimeSpan.FromSeconds(o.TimeoutSeconds),
            ranges,
            string.IsNullOrEmpty(o.ExpectBodyContains) ? null : o.ExpectBodyContains,
            o.CertificateWarnDays,
            TimeSpan.FromSeconds(o.IntervalSeconds),
            o.FailuresBeforeAlert);
        return true;
    }

    /// <summary>Validates the whole list (including unique names). Used by StartupValidator.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<EndpointCheckOptions> endpoints)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < endpoints.Count; i++)
        {
            var key = $"Monitoring:Endpoints:{i.ToString(CultureInfo.InvariantCulture)}";
            if (!TryCreate(endpoints[i], key, out var check, out var error))
                errors.Add(error);
            else if (!names.Add(check.Name))
                errors.Add($"{key}:Name '{check.Name}' is used by more than one endpoint.");
        }
        return errors;
    }

    public static bool TryParseStatusRanges(string? value, out IReadOnlyList<(int From, int To)> ranges)
    {
        var list = new List<(int, int)>();
        ranges = list;
        if (string.IsNullOrWhiteSpace(value))
        {
            list.Add((200, 399));
            return true;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2 ||
                !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from) ||
                !int.TryParse(bounds[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var to) ||
                from < 100 || to > 599 || from > to)
                return false;
            list.Add((from, to));
        }
        return list.Count > 0;
    }

    /// <summary>The URL without user info, query or fragment.</summary>
    public static string Display(Uri url) => url.GetComponents(
        UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    private static bool TrySplitHostPort(string value, int? defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort ?? 0;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/', StringComparison.Ordinal))
            return false;

        string portText;
        if (value.StartsWith('['))
        {
            // [IPv6]:port
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return false;
            host = value[1..close];
            portText = value.Length > close + 1 && value[close + 1] == ':' ? value[(close + 2)..] : string.Empty;
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon >= 0 && value.IndexOf(':') != colon)
                return false; // bare IPv6 without brackets is ambiguous
            host = colon >= 0 ? value[..colon] : value;
            portText = colon >= 0 ? value[(colon + 1)..] : string.Empty;
        }

        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;
        if (portText.Length == 0)
            return defaultPort.HasValue;
        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65_535;
    }

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}