using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace WatchTower.Services.Endpoints;

public enum EndpointState
{
    Up,
    Down,

    /// <summary>Certificate valid but expiring within the warning window.</summary>
    Expiring,
}

/// <summary>Outcome of one check.</summary>
public sealed record EndpointProbeResult(
    EndpointState State,
    string Detail,
    double? ResponseTimeMs = null,
    double? CertificateDaysLeft = null);

/// <summary>Runs one endpoint check.</summary>
public interface IEndpointProbe
{
    /// <summary>Never throws for endpoint failures (only when <paramref name="ct"/> is cancelled).</summary>
    Task<EndpointProbeResult> ProbeAsync(EndpointCheck check, CancellationToken ct);
}

/// <summary>
/// HTTP GET through <see cref="IHttpClientFactory"/>, TCP connect, and a TLS handshake that
/// reads the server certificate's expiry. Redirects are followed manually, at most
/// <see cref="MaxRedirects"/> times and only within the same host (http→https allowed); a
/// redirect elsewhere is evaluated as the final response.
/// </summary>
public sealed class EndpointProbe(IHttpClientFactory httpClientFactory, TimeProvider time) : IEndpointProbe
{
    public const string HttpClientName = "EndpointChecks";
    public const int MaxRedirects = 5;
    private const int MaxBodyChars = 1024 * 1024;

    public async Task<EndpointProbeResult> ProbeAsync(EndpointCheck check, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(check.Timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return check.Type switch
            {
                EndpointCheckType.Http => await ProbeHttpAsync(check, stopwatch, cts.Token),
                EndpointCheckType.Tcp => await ProbeTcpAsync(check, stopwatch, cts.Token),
                _ => await ProbeCertificateAsync(check, stopwatch, cts.Token),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EndpointProbeResult(EndpointState.Down,
                $"Timed out after {check.Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or AuthenticationException)
        {
            return new EndpointProbeResult(EndpointState.Down, Describe(ex), Elapsed(stopwatch));
        }
    }

    private async Task<EndpointProbeResult> ProbeHttpAsync(EndpointCheck check, Stopwatch stopwatch, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var uri = check.Url!;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var elapsed = Elapsed(stopwatch);

            if (IsRedirect(response.StatusCode) && hop < MaxRedirects && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (IsSameSiteRedirect(uri, next))
                {
                    uri = next;
                    continue;
                }
            }

            var status = (int)response.StatusCode;
            if (!check.IsExpectedStatus(status))
                return new EndpointProbeResult(EndpointState.Down, $"Unexpected HTTP {status} {response.ReasonPhrase}".TrimEnd(), elapsed);

            if (check.ExpectBodyContains is { } expected)
            {
                var body = await ReadBodyAsync(response, ct);
                if (!body.Contains(expected, StringComparison.Ordinal))
                    return new EndpointProbeResult(EndpointState.Down, $"HTTP {status}, but the response does not contain the expected text", elapsed);
            }

            return new EndpointProbeResult(EndpointState.Up, $"HTTP {status}", elapsed);
        }
    }

    private static async Task<EndpointProbeResult> ProbeTcpAsync(EndpointCheck check, Stopwatch stopwatch, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(check.Host!, check.Port, ct);
        return new EndpointProbeResult(EndpointState.Up, "Connected", Elapsed(stopwatch));
    }

    private async Task<EndpointProbeResult> ProbeCertificateAsync(EndpointCheck check, Stopwatch stopwatch, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(check.Host!, check.Port, ct);

        DateTime? notAfter = null;
        string? subject = null;
        var policyErrors = SslPolicyErrors.None;
        var chainProblems = X509ChainStatusFlags.NoError;

        // Accept any certificate so it can be inspected; validity is judged below.
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, chain, errors) =>
        {
            if (certificate is X509Certificate2 cert)
            {
                notAfter = cert.NotAfter.ToUniversalTime();
                subject = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            }
            policyErrors = errors;
            foreach (var status in chain?.ChainStatus ?? [])
                chainProblems |= status.Status;
            return true;
        });

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = check.Host,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, ct);

        var elapsed = Elapsed(stopwatch);
        if (notAfter is not { } expiry)
            return new EndpointProbeResult(EndpointState.Down, "The server presented no certificate", elapsed);

        var result = Classify(expiry, policyErrors, chainProblems, subject, time.GetUtcNow().UtcDateTime, check.CertificateWarnDays);
        return result with { ResponseTimeMs = elapsed };
    }

    /// <summary>
    /// Judges a certificate: invalid or expired = Down, inside the warning window = Expiring.
    /// The state is decided on the exact remaining time (expired only once <paramref name="nowUtc"/>
    /// is past NotAfter); the day count is rounded only for display and the stored metric.
    /// </summary>
    public static EndpointProbeResult Classify(
        DateTime notAfterUtc, SslPolicyErrors policyErrors, X509ChainStatusFlags chainProblems, string? subject,
        DateTime nowUtc, int warnDays)
    {
        var remaining = notAfterUtc - nowUtc;
        var daysLeft = Math.Round(remaining.TotalDays, 1);
        var expires = notAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var who = string.IsNullOrEmpty(subject) ? "Certificate" : $"Certificate for {subject}";

        if (remaining <= TimeSpan.Zero)
            return new EndpointProbeResult(EndpointState.Down, $"{who} expired on {expires}", CertificateDaysLeft: daysLeft);

        if (policyErrors != SslPolicyErrors.None)
        {
            var problems = policyErrors.ToString();
            if (chainProblems != X509ChainStatusFlags.NoError)
                problems += $" ({chainProblems})";
            return new EndpointProbeResult(EndpointState.Down, $"{who} is not valid: {problems}", CertificateDaysLeft: daysLeft);
        }

        var days = daysLeft.ToString("0.#", CultureInfo.InvariantCulture);
        if (remaining >= TimeSpan.FromDays(warnDays))
            return new EndpointProbeResult(EndpointState.Up, $"{who} valid for {days} more days ({expires})", CertificateDaysLeft: daysLeft);

        // Under a day the rounded count can read "0 days": show the time left instead.
        var left = remaining < TimeSpan.FromDays(1) ? Alerting.AlertText.FormatDuration(remaining) : $"{days} days";
        return new EndpointProbeResult(EndpointState.Expiring, $"{who} expires in {left} ({expires})", CertificateDaysLeft: daysLeft);
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static bool IsSameSiteRedirect(Uri from, Uri to) =>
        string.Equals(from.IdnHost, to.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        (to.Scheme == from.Scheme || (from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps));

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxBodyChars];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, read);
    }

    private static double Elapsed(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: AuthenticationException auth } => $"TLS handshake failed: {auth.Message}",
        HttpRequestException http when http.HttpRequestError != HttpRequestError.Unknown => $"{http.HttpRequestError}: {http.Message}",
        SocketException socket => $"Connection failed: {socket.SocketErrorCode}",
        AuthenticationException auth => $"TLS handshake failed: {auth.Message}",
        _ => ex.Message,
    };
}