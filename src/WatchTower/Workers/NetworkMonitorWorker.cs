using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

public class NetworkMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IAlertManager _alerts;
    private readonly IOptionsMonitor<NetworkMonitorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NetworkMonitorWorker> _logger;
    private readonly IWorkerHealth? _health;

    private readonly RecoveryDebouncer _recovery = new();
    private readonly AbsentResourceTracker _removedAdapters;

    public NetworkMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IOptionsMonitor<NetworkMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<NetworkMonitorWorker> logger,
        IWorkerHealth? workerHealth = null
    )
    {
        _metricsProvider = metricsProvider;
        _metricsStore = metricsStore;
        _alerts = alerts;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _health = workerHealth;

        // Zero adapters can be genuine (all disabled), so it counts; the grace period still applies.
        // Removed adapters also lose their snapshot row, so the portal stops receiving them.
        _removedAdapters = new AbsentResourceTracker(AlertKey.NetworkPrefix, "network adapters",
            adapter => $"Network adapter {adapter} is no longer present", emptyIsSuspicious: false, logger,
            metricsStore, SnapshotKind.Network);
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Network Monitor Worker started");

        ReportAlive();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                ReportAlive();
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)),
                    _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Network Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Network Monitor Worker stopped");
    }

    /// <summary>Liveness for the health-gated heartbeat (see <see cref="IWorkerHealth"/>).</summary>
    private void ReportAlive() =>
        _health?.ReportSuccess(WorkerNames.Network, TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)));

    /// <summary>Runs a single poll/evaluate cycle. Exposed for deterministic testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        IReadOnlyList<NetworkMetric> networkMetrics;
        try
        {
            networkMetrics = _metricsProvider.GetNetworkMetrics();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading network metrics failed; skipping this cycle");
            return;
        }

        foreach (var metric in networkMetrics)
        {
            _metricsStore.AddNetworkMetric(metric);
            var key = AlertKey.Network(metric.InterfaceName);

            if (metric.OutputQueueLength > opts.OutputQueueWarningThreshold)
            {
                _logger.LogWarning(
                    "NETWORK QUEUE SATURATED: {Interface} output queue {Queue:F0} (threshold: > {Threshold})",
                    metric.InterfaceName, metric.OutputQueueLength, opts.OutputQueueWarningThreshold);
                _recovery.Breached(key);
                await _alerts.RaiseAsync(
                    key,
                    AlertSeverity.Warning,
                    $"Network Saturated: {metric.InterfaceName}",
                    new Incident(
                        UtcNow,
                        IncidentType.NetworkQueueSaturated,
                        $"Network interface '{metric.InterfaceName}' output queue at {metric.OutputQueueLength:F0} (threshold: > {opts.OutputQueueWarningThreshold})"),
                    ct);
            }
            else
            {
                _logger.LogDebug(
                    "Network {Interface}: output queue {Queue:F0}",
                    metric.InterfaceName, metric.OutputQueueLength);
                if (_recovery.Clear(key, UtcNow, _alerts.RecoveryWindow))
                    await _alerts.ResolveAsync(key, ct: ct);
            }
        }

        // Only after the metrics read succeeded (see AbsentResourceTracker).
        try
        {
            await _removedAdapters.ReconcileAsync(_alerts, _metricsProvider.GetNetworkInventory, UtcNow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not retire alerts or snapshots of removed network adapters");
        }
    }
}