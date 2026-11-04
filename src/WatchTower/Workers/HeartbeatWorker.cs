using System.Text;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;

namespace WatchTower.Workers;

/// <summary>
/// Dead man's switch. Pings the configured URL (healthchecks.io style) every interval — but only
/// reports success while every monitoring worker is alive: when any of them has had no
/// successful cycle for its staleness limit, it pings <c>&lt;url&gt;/fail</c> with a short reason
/// instead, so a hung or crash-looping monitor is noticed even though the process is running.
/// </summary>
public class HeartbeatWorker : BackgroundService
{
    private const int MaxReasonLength = 1000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private readonly IWorkerHealth _workerHealth;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HeartbeatOptions> options,
        IWorkerHealth workerHealth,
        TimeProvider timeProvider,
        ILogger<HeartbeatWorker> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _workerHealth = workerHealth;
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

        var stale = _workerHealth.GetStale();
        if (stale.Count == 0)
        {
            await PingAsync(() => new HttpRequestMessage(HttpMethod.Get, url), "Heartbeat ping", ct);
            return;
        }

        var reason = FailureReason(stale, _timeProvider.GetUtcNow());
        _logger.LogWarning("Monitoring is unhealthy; reporting failure to the dead man's switch: {Reason}", reason);
        var failUrl = FailUrl(url);
        await PingAsync(() => new HttpRequestMessage(HttpMethod.Post, failUrl)
        {
            Content = new StringContent(reason, Encoding.UTF8, "text/plain"),
        }, "Heartbeat failure ping", ct);
    }

    /// <summary><c>https://hc-ping.com/&lt;uuid&gt;</c> → <c>https://hc-ping.com/&lt;uuid&gt;/fail</c> (query kept).</summary>
    internal static string FailUrl(string url)
    {
        var builder = new UriBuilder(url.Trim());
        builder.Path = builder.Path.TrimEnd('/') + "/fail";
        return builder.Uri.AbsoluteUri;
    }

    internal static string FailureReason(IReadOnlyList<WorkerHealthEntry> stale, DateTimeOffset now)
    {
        var reason = "WatchTower monitoring stalled: " + string.Join(", ", stale.Select(s =>
            $"{s.Worker} (no successful cycle for {Services.Alerting.AlertText.FormatDuration((now - s.LastSuccessAt).Duration())})"));
        return reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
    }

    private async Task PingAsync(Func<HttpRequestMessage> request, string what, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("Heartbeat");
        try
        {
            using var response = await client.SendAsync(request(), ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("{What} successful", what);
                return;
            }
            _logger.LogWarning("{What} returned {StatusCode}, retrying once", what, response.StatusCode);
        }
        // Treat both connection failures and the HttpClient timeout (a TaskCanceledException
        // NOT caused by our stopping token) as a ping failure, so a slow endpoint does not
        // bubble out as an unclassified worker error and reset the interval.
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "{What} failed, retrying once", what);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, ct);

        try
        {
            using var response = await client.SendAsync(request(), ct);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("{What} retry successful", what);
            else
                _logger.LogError("{What} retry failed with {StatusCode}", what, response.StatusCode);
        }
        catch (Exception retryEx) when (retryEx is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(retryEx, "{What} retry also failed", what);
        }
    }
}