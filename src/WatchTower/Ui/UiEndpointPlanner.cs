using System.Net;

namespace WatchTower.Ui;

/// <summary>A validated address the console will listen on.</summary>
public sealed record UiEndpoint(IPAddress Address, int Port, bool Https, bool IsLoopback, string Display);

/// <summary>
/// Turns <see cref="UiOptions.Urls"/> into concrete listen endpoints, enforcing the security
/// policy: loopback by default; anything reachable from the network must be HTTPS with a
/// certificate and explicitly allowed. Invalid entries are dropped with a reason — they never
/// prevent the monitoring service itself from starting.
/// </summary>
public static class UiEndpointPlanner
{
    public sealed record Result(IReadOnlyList<UiEndpoint> Endpoints, IReadOnlyList<string> Problems);

    public static Result Plan(UiOptions options)
    {
        var endpoints = new List<UiEndpoint>();
        var problems = new List<string>();

        if (!options.Enabled)
            return new Result(endpoints, problems);

        var urls = options.Urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0)
            urls.Add(UiOptions.DefaultUrl);

        foreach (var raw in urls)
        {
            // Accept Kestrel-style wildcards (http://*:8750, http://+:8750).
            var normalized = raw.Trim().Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0");
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.AbsolutePath != "/")
            {
                problems.Add($"'{raw}' is not a valid listen URL (expected e.g. http://127.0.0.1:8750).");
                continue;
            }

            IPAddress address;
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                address = IPAddress.Loopback;
            else if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out address!))
            {
                problems.Add($"'{raw}': use an IP address (or localhost), not a host name.");
                continue;
            }

            var https = uri.Scheme == Uri.UriSchemeHttps;
            var loopback = IPAddress.IsLoopback(address);

            if (!loopback)
            {
                if (!options.AllowRemoteAccess)
                {
                    problems.Add($"'{raw}' is reachable from the network but Ui:AllowRemoteAccess is off; ignored.");
                    continue;
                }
                if (!https && options.RequireHttpsForRemoteAccess)
                {
                    problems.Add($"'{raw}': remote access requires https:// while Ui:RequireHttpsForRemoteAccess " +
                                 "is on (turn it off to allow plain HTTP).");
                    continue;
                }
            }

            if (https && string.IsNullOrWhiteSpace(options.CertificateThumbprint))
            {
                problems.Add($"'{raw}' uses https:// but Ui:CertificateThumbprint is not set; ignored.");
                continue;
            }

            endpoints.Add(new UiEndpoint(address, uri.Port, https, loopback, $"{uri.Scheme}://{uri.Authority}"));
        }

        if (endpoints.Count == 0 && problems.Count > 0)
            problems.Add("No valid listen URL remains, so the web console is not started.");

        return new Result(endpoints, problems);
    }
}