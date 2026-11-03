using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Clients;

public class HttpsPortalClient : IPortalClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger<HttpsPortalClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpsPortalClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<PortalOptions> options,
        ILogger<HttpsPortalClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;

        ValidateTls(options.CurrentValue);
    }

    private void ValidateTls(PortalOptions opts)
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
            var client = CreateClient();
            var response = await client.GetAsync(BuildUrl("api/v1/agent/commands/pending"), ct);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return Array.Empty<AgentCommandDto>();

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
            ack, ct);
    }

    public async Task<bool> ReportCommandResultAsync(CommandResultDto result, CancellationToken ct)
    {
        return await PostAsync(
            $"api/v1/agent/commands/{Uri.EscapeDataString(result.CommandId)}/result",
            result, ct);
    }

    public async Task<(bool HasUpdate, RemoteAgentConfigDto? Config, string? Version)>
        FetchConfigurationAsync(string? currentVersion, CancellationToken ct)
    {
        try
        {
            var client = CreateClient();
            var url = BuildUrl("api/v1/agent/config");
            if (!string.IsNullOrEmpty(currentVersion))
                url += $"?version={Uri.EscapeDataString(currentVersion)}";

            var response = await client.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return (false, null, null);

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

    private async Task<bool> PostAsync<T>(string path, T payload, CancellationToken ct)
    {
        try
        {
            var client = CreateClient();
            using var response = await client.PostAsJsonAsync(BuildUrl(path), payload, JsonOpts, ct);

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

    private HttpClient CreateClient()
    {
        // Note: the HTTPS transport always validates server certificates. AllowUntrustedCert
        // is intentionally NOT honored here (it only affects the gRPC transport, guarded to
        // non-production there) so HTTPS fails closed.
        return _httpClientFactory.CreateClient("PortalClient");
    }

    private record ConfigFetchResponse(RemoteAgentConfigDto? Config, string? Version);
}