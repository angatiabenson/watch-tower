using Microsoft.Extensions.Options;
using WatchTower.Configuration;

namespace WatchTower.Workers;

public class HeartbeatWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HeartbeatOptions> options,
        ILogger<HeartbeatWorker> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;

                if (string.IsNullOrWhiteSpace(opts.Url))
                {
                    _logger.LogDebug("Heartbeat URL not configured, skipping");
                    await Task.Delay(TimeSpan.FromSeconds(opts.IntervalSeconds), stoppingToken);
                    continue;
                }

                await PingAsync(opts.Url, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(opts.IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Heartbeat Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Heartbeat Worker stopped");
    }

    private async Task PingAsync(string url, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("Heartbeat");
        try
        {
            var response = await client.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Heartbeat ping successful");
            }
            else
            {
                _logger.LogWarning(
                    "Heartbeat ping returned {StatusCode}, retrying once",
                    response.StatusCode
                );
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Heartbeat retry successful");
                else
                    _logger.LogError(
                        "Heartbeat retry failed with {StatusCode}",
                        response.StatusCode
                    );
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Heartbeat ping failed, retrying once");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            try
            {
                var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Heartbeat retry successful");
                else
                    _logger.LogError(
                        "Heartbeat retry failed with {StatusCode}",
                        response.StatusCode
                    );
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "Heartbeat retry also failed");
            }
        }
    }
}
