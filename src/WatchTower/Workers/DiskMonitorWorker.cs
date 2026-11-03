using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

public class DiskMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IAlertManager _alerts;
    private readonly IOptionsMonitor<DiskMonitorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiskMonitorWorker> _logger;
    private readonly IWorkerHealth? _health;

    private readonly RecoveryDebouncer _recovery = new();

    public DiskMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IOptionsMonitor<DiskMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<DiskMonitorWorker> logger,
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
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Disk Monitor Worker started");

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
                _logger.LogError(ex, "Error in Disk Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Disk Monitor Worker stopped");
    }

    /// <summary>Liveness for the health-gated heartbeat (see <see cref="IWorkerHealth"/>).</summary>
    private void ReportAlive() =>
        _health?.ReportSuccess(WorkerNames.Disk, TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)));

    /// <summary>Runs a single poll/evaluate cycle. Exposed for deterministic testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!TryRead(_metricsProvider.GetDiskMetrics, "disk usage", out var diskMetrics))
            return;

        foreach (var disk in diskMetrics)
        {
            _metricsStore.AddDiskMetric(disk);
            var key = AlertKey.Disk(disk.DriveName);

            if (disk.UsagePercent >= opts.CriticalThresholdPercent)
            {
                _logger.LogWarning("DISK CRITICAL: {Drive} at {Usage:F1}%", disk.DriveName, disk.UsagePercent);
                _recovery.Breached(key);
                await _alerts.RaiseAsync(
                    key,
                    AlertSeverity.Critical,
                    $"Disk Critical: {disk.DriveName} at {disk.UsagePercent:F1}%",
                    new Incident(
                        UtcNow,
                        IncidentType.DiskCritical,
                        $"Drive {disk.DriveName} at {disk.UsagePercent:F1}% ({disk.FreeGb:F1} GB free of {disk.TotalGb:F1} GB)"),
                    ct);
                continue;
            }

            if (disk.UsagePercent >= opts.WarningThresholdPercent)
                _logger.LogWarning("DISK WARNING: {Drive} at {Usage:F1}%", disk.DriveName, disk.UsagePercent);
            else
                _logger.LogDebug("Disk {Drive}: {Usage:F1}% ({Free:F1} GB free)", disk.DriveName, disk.UsagePercent, disk.FreeGb);

            if (_recovery.Clear(key, UtcNow, _alerts.RecoveryWindow))
                await _alerts.ResolveAsync(key, ct: ct);
        }

        // Disk queue length monitoring
        if (!TryRead(_metricsProvider.GetDiskQueueMetrics, "disk queue length", out var queueMetrics))
            return;

        foreach (var queue in queueMetrics)
        {
            _metricsStore.AddDiskQueueMetric(queue);
            var key = AlertKey.DiskQueue(queue.InstanceName);

            if (queue.QueueLength > opts.QueueLengthWarningThreshold)
            {
                _logger.LogWarning(
                    "DISK QUEUE HIGH: {Disk} queue length {Queue:F1} (threshold: {Threshold})",
                    queue.InstanceName, queue.QueueLength, opts.QueueLengthWarningThreshold);
                _recovery.Breached(key);
                await _alerts.RaiseAsync(
                    key,
                    AlertSeverity.Warning,
                    $"Disk Queue High: {queue.InstanceName} at {queue.QueueLength:F1}",
                    new Incident(
                        UtcNow,
                        IncidentType.DiskQueueHigh,
                        $"Disk '{queue.InstanceName}' queue length at {queue.QueueLength:F1} (threshold: {opts.QueueLengthWarningThreshold})"),
                    ct);
                continue;
            }

            _logger.LogDebug("Disk queue {Disk}: {Queue:F1}", queue.InstanceName, queue.QueueLength);
            if (_recovery.Clear(key, UtcNow, _alerts.RecoveryWindow))
                await _alerts.ResolveAsync(key, ct: ct);
        }
    }

    /// <summary>
    /// Reads a metric set, skipping the rest of the cycle with a warning (instead of failing the
    /// iteration) when the underlying source throws.
    /// </summary>
    private bool TryRead<T>(Func<IReadOnlyList<T>> read, string what, out IReadOnlyList<T> metrics)
    {
        try
        {
            metrics = read();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading {What} failed; skipping this cycle", what);
            metrics = Array.Empty<T>();
            return false;
        }
    }
}