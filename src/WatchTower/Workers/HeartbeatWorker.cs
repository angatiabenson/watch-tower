using Microsoft.Extensions.Options;

using WatchTower.Configuration;

namespace WatchTower.Workers;

public class HeartbeatWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HeartbeatOptions> options,
        TimeProvider timeProvider,
        ILogger<HeartbeatWorker> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PingOnceAsync(stoppingToken);
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.IntervalSeconds)),
                    _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Heartbeat Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Heartbeat Worker stopped");
    }

    /// <summary>Performs a single heartbeat cycle (skips when no URL is configured). Exposed for testing.</summary>
    internal async Task PingOnceAsync(CancellationToken ct)
    {
        var url = _options.CurrentValue.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogDebug("Heartbeat URL not configured, skipping");
            return;
        }

        await PingAsync(url, ct);
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
                _logger.LogWarning("Heartbeat ping returned {StatusCode}, retrying once", response.StatusCode);
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, ct);

                response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Heartbeat retry successful");
                else
                    _logger.LogError("Heartbeat retry failed with {StatusCode}", response.StatusCode);
            }
        }
        // Treat both connection failures and the HttpClient timeout (a TaskCanceledException
        // NOT caused by our stopping token) as a ping failure, so a slow endpoint does not
        // bubble out as an unclassified worker error and reset the interval.
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Heartbeat ping failed, retrying once");
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, ct);

            try
            {
                var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Heartbeat retry successful");
                else
                    _logger.LogError("Heartbeat retry failed with {StatusCode}", response.StatusCode);
            }
            catch (Exception retryEx) when (retryEx is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(retryEx, "Heartbeat retry also failed");
            }
        }
    }
}