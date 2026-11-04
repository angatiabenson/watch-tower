using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Clients;

/// <summary>
/// The HTTPS (and, with <c>Tls:RequireTls=false</c>, plain HTTP) portal transport.
/// </summary>
/// <remarks>
/// Redirects are never followed (see <see cref="PortalTls.CreateHandler"/>). Any 3xx answer is a
/// failure of that call, handled like any other failed status: POSTs return <c>false</c> (so the
/// caller's normal retry applies) and GETs return "nothing". The redirect is logged as a warning
/// with its target, at most once per <see cref="RedirectWarningInterval"/> per endpoint.
/// </remarks>
public class HttpsPortalClient : IPortalClient
{
    /// <summary>Minimum time between two redirect warnings for the same portal endpoint.</summary>
    internal static readonly TimeSpan RedirectWarningInterval = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger<HttpsPortalClient> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly object _redirectLogLock = new();
    private readonly Dictionary<string, (DateTimeOffset LoggedAt, int Suppressed)> _redirectLog = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpsPortalClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<PortalOptions> options,
        ILogger<HttpsPortalClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        PortalTls.EnsureTransportAllowed(options.CurrentValue);
    }

    private string BuildUrl(string path)
    {
        var base_ = _options.CurrentValue.BaseUrl.TrimEnd('/');
        return $"{base_}/{path}";
    }

    public async Task<bool> SendMetricsBatchAsync(MetricsBatchDto batch, CancellationToken ct)
    {
        return await PostAsync("api/v1/agent/metrics/batch", batch, ct);
    }

    public async Task<bool> SendEventAsync(AgentEventDto evt, CancellationToken ct)
    {
        return await PostAsync("api/v1/agent/events", evt, ct);
    }

    public async Task<IReadOnlyList<AgentCommandDto>> PollCommandsAsync(CancellationToken ct)
    {
        try
        {
            const string path = "api/v1/agent/commands/pending";
            var client = CreateClient();
            using var response = await client.GetAsync(BuildUrl(path), ct);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return Array.Empty<AgentCommandDto>();

            if (IsRedirect(response))
            {
                LogRedirect(path, path, response);
                return Array.Empty<AgentCommandDto>();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PollCommands returned {StatusCode}", (int)response.StatusCode);
                return Array.Empty<AgentCommandDto>();
            }

            var commands = await response.Content.ReadFromJsonAsync<List<AgentCommandDto>>(
                JsonOpts, ct);
            return commands ?? new List<AgentCommandDto>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PollCommands failed");
            return Array.Empty<AgentCommandDto>();
        }
    }

    public async Task<bool> AcknowledgeCommandAsync(AcknowledgeCommandDto ack, CancellationToken ct)
    {
        return await PostAsync(
            $"api/v1/agent/commands/{Uri.EscapeDataString(ack.CommandId)}/acknowledge",
            ack, ct, "api/v1/agent/commands/{commandId}/acknowledge");
    }

    public async Task<bool> ReportCommandResultAsync(CommandResultDto result, CancellationToken ct)
    {
        return await PostAsync(
            $"api/v1/agent/commands/{Uri.EscapeDataString(result.CommandId)}/result",
            result, ct, "api/v1/agent/commands/{commandId}/result");
    }

    public async Task<(bool HasUpdate, RemoteAgentConfigDto? Config, string? Version)>
        FetchConfigurationAsync(string? currentVersion, CancellationToken ct)
    {
        try
        {
            const string path = "api/v1/agent/config";
            var client = CreateClient();
            var url = BuildUrl(path);
            if (!string.IsNullOrEmpty(currentVersion))
                url += $"?version={Uri.EscapeDataString(currentVersion)}";

            using var response = await client.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return (false, null, null);

            if (IsRedirect(response))
            {
                LogRedirect(path, path, response);
                return (false, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FetchConfiguration returned {StatusCode}", (int)response.StatusCode);
                return (false, null, null);
            }

            var wrapper = await response.Content
                .ReadFromJsonAsync<ConfigFetchResponse>(JsonOpts, ct);

            return (true, wrapper?.Config, wrapper?.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FetchConfiguration failed");
            return (false, null, null);
        }
    }

    public async Task<bool> AcknowledgeConfigAsync(string version, CancellationToken ct)
    {
        return await PostAsync("api/v1/agent/config/acknowledge",
            new { version }, ct);
    }

    /// <param name="path">Path below BaseUrl that is requested.</param>
    /// <param name="payload">JSON body.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="endpoint">Endpoint template for rate-limiting the redirect warning (defaults to <paramref name="path"/>).</param>
    private async Task<bool> PostAsync<T>(string path, T payload, CancellationToken ct, string? endpoint = null)
    {
        try
        {
            var client = CreateClient();
            using var response = await client.PostAsJsonAsync(BuildUrl(path), payload, JsonOpts, ct);

            if (IsRedirect(response))
            {
                // Not delivered: a redirect is never followed, so nothing reached the portal.
                LogRedirect(endpoint ?? path, path, response);
                return false;
            }

            if (response.IsSuccessStatusCode)
                return true;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError(
                    "Portal returned 401 Unauthorized on {Path}. Check AgentToken.", path);
                return false;
            }

            _logger.LogWarning(
                "Portal request to {Path} returned {StatusCode}", path, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Portal request to {Path} failed", path);
            return false;
        }
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and <= 399;

    /// <summary>
    /// Warns that the portal answered <paramref name="path"/> with a redirect, at most once per
    /// <see cref="RedirectWarningInterval"/> for each <paramref name="endpoint"/>; the redirects in
    /// between are logged at debug level and counted in the next warning.
    /// </summary>
    private void LogRedirect(string endpoint, string path, HttpResponseMessage response)
    {
        var location = RedirectTarget(response);
        var status = (int)response.StatusCode;
        var now = _timeProvider.GetUtcNow();

        int suppressed;
        lock (_redirectLogLock)
        {
            if (_redirectLog.TryGetValue(endpoint, out var last) && now - last.LoggedAt < RedirectWarningInterval)
            {
                _redirectLog[endpoint] = (last.LoggedAt, last.Suppressed + 1);
                suppressed = -1;
            }
            else
            {
                suppressed = last.Suppressed;
                _redirectLog[endpoint] = (now, 0);
            }
        }

        if (suppressed < 0)
        {
            _logger.LogDebug("Portal redirected {Path} to {Location} ({StatusCode}); not followed", path, location, status);
            return;
        }

        _logger.LogWarning(
            "Portal redirected {Path} to {Location}; update {Section}:BaseUrl. The redirect ({StatusCode}) " +
            "was not followed and the call counts as failed ({Suppressed} more redirect(s) of this " +
            "endpoint since the last warning)",
            path, location, PortalOptions.SectionName, status, suppressed);
    }

    /// <summary>The redirect's Location, made absolute against the request, or "(no Location header)".</summary>
    private static string RedirectTarget(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return "(no Location header)";
        if (!location.IsAbsoluteUri && response.RequestMessage?.RequestUri is { } requestUri)
            location = new Uri(requestUri, location);
        return location.IsAbsoluteUri ? location.AbsoluteUri : location.OriginalString;
    }

    private HttpClient CreateClient()
    {
        // Note: the HTTPS transport always validates server certificates. AllowUntrustedCert
        // is intentionally NOT honored here (it only affects the gRPC transport, guarded to
        // non-production there) so HTTPS fails closed. The mutual-TLS client certificate
        // (Tls.ClientCertThumbprint) is attached to the "PortalClient" registration via
        // ConfigurePrimaryHttpMessageHandler + PortalTls.CreateHandler (see PortalTls).
        return _httpClientFactory.CreateClient("PortalClient");
    }

    private record ConfigFetchResponse(RemoteAgentConfigDto? Config, string? Version);
}