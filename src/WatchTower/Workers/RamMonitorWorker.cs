using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

public class RamMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IAlertManager _alerts;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<RamMonitorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RamMonitorWorker> _logger;
    private readonly IWorkerHealth? _health;

    private readonly RecoveryDebouncer _recovery = new();
    private DateTime? _firstBreachTime;
    private bool _metricUnavailable;

    public RamMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<RamMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<RamMonitorWorker> logger,
        IWorkerHealth? workerHealth = null
    )
    {
        _metricsProvider = metricsProvider;
        _metricsStore = metricsStore;
        _alerts = alerts;
        _portalEventPublisher = portalEventPublisher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _health = workerHealth;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RAM Monitor Worker started");

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
                _logger.LogError(ex, "Error in RAM Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("RAM Monitor Worker stopped");
    }

    /// <summary>Liveness for the health-gated heartbeat (see <see cref="IWorkerHealth"/>).</summary>
    private void ReportAlive() =>
        _health?.ReportSuccess(WorkerNames.Ram, TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)));

    /// <summary>Runs a single poll/evaluate cycle. Exposed for deterministic testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!TryReadRam(out var ramMetric))
            return;

        _metricsStore.AddRamMetric(ramMetric);

        _logger.LogDebug(
            "RAM: {Usage:F1}% ({Available:F0} MB available / {Total:F0} MB total)",
            ramMetric.UsagePercent,
            ramMetric.AvailableMb,
            ramMetric.TotalMb
        );

        if (ramMetric.UsagePercent >= opts.ThresholdPercent)
        {
            _recovery.Breached(AlertKey.Ram);

            if (_firstBreachTime == null)
            {
                _firstBreachTime = UtcNow;

                // Log spike incident immediately with top processes
                var topProcs = (await _metricsProvider.GetTopMemoryProcessesAsync(3, ct)).ToList();
                var spike = new Incident(
                    UtcNow,
                    IncidentType.RamSpikeDetected,
                    $"RAM hit {ramMetric.UsagePercent:F1}% ({ramMetric.AvailableMb:F0} MB available, threshold: {opts.ThresholdPercent}%)",
                    topProcs
                );
                _metricsStore.AddIncident(spike);
                await _portalEventPublisher.PublishIncidentAsync(spike, ct);
                _logger.LogWarning("RAM spike detected: {Description}", spike.Description);
            }

            var elapsed = UtcNow - _firstBreachTime.Value;
            if (elapsed.TotalSeconds >= opts.SustainedSeconds)
            {
                await RaiseEmergencyAsync(ramMetric, opts, ct);
                _firstBreachTime = UtcNow;
            }
        }
        else
        {
            _firstBreachTime = null;
            if (_recovery.Clear(AlertKey.Ram, UtcNow, _alerts.RecoveryWindow))
                await _alerts.ResolveAsync(AlertKey.Ram, ct: ct);
        }
    }

    /// <summary>
    /// Reads RAM usage, tolerating an unavailable metric source (NaN usage or a throwing
    /// provider): the cycle is skipped with a warning on the first miss of a streak.
    /// </summary>
    private bool TryReadRam([NotNullWhen(true)] out RamMetric? ramMetric)
    {
        RamMetric? read = null;
        try
        {
            read = _metricsProvider.GetRamMetric();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading RAM usage threw");
        }

        if (read != null && double.IsFinite(read.UsagePercent))
        {
            if (_metricUnavailable)
                _logger.LogInformation("RAM usage is available again");
            _metricUnavailable = false;
            ramMetric = read;
            return true;
        }

        if (!_metricUnavailable)
            _logger.LogWarning("RAM usage is unavailable; skipping this cycle");
        _metricUnavailable = true;
        ramMetric = null;
        return false;
    }

    /// <summary>Sustained breach: raise (or keep open) the <c>ram</c> alert; see CpuMonitorWorker.</summary>
    private Task RaiseEmergencyAsync(RamMetric ramMetric, RamMonitorOptions opts, CancellationToken ct) =>
        _alerts.RaiseAsync(
            AlertKey.Ram,
            AlertSeverity.Critical,
            $"RAM Emergency: {ramMetric.UsagePercent:F1}%",
            async token =>
            {
                var topProcesses = (await _metricsProvider.GetTopMemoryProcessesAsync(opts.TopProcessCount, token)).ToList();
                return new Incident(
                    UtcNow,
                    IncidentType.RamThresholdBreached,
                    $"RAM at {ramMetric.UsagePercent:F1}% ({ramMetric.AvailableMb:F0} MB available) for over {opts.SustainedSeconds}s",
                    topProcesses
                );
            },
            ct);
}