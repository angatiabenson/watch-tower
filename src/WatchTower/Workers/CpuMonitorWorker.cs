using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

public class CpuMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IAlertManager _alerts;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<CpuMonitorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CpuMonitorWorker> _logger;

    private readonly RecoveryDebouncer _recovery = new();
    private DateTime? _firstBreachTime;
    private bool _metricUnavailable;

    public CpuMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<CpuMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<CpuMonitorWorker> logger
    )
    {
        _metricsProvider = metricsProvider;
        _metricsStore = metricsStore;
        _alerts = alerts;
        _portalEventPublisher = portalEventPublisher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CPU Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
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
                _logger.LogError(ex, "Error in CPU Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("CPU Monitor Worker stopped");
    }

    /// <summary>Runs a single poll/evaluate cycle. Exposed for deterministic testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!TryReadCpu(out var cpuUsage))
            return;

        var metric = new CpuMetric(UtcNow, cpuUsage);
        _metricsStore.AddCpuMetric(metric);

        _logger.LogDebug("CPU: {Usage:F1}%", cpuUsage);

        if (cpuUsage >= opts.ThresholdPercent)
        {
            _recovery.Breached(AlertKey.Cpu);

            if (_firstBreachTime == null)
            {
                _firstBreachTime = UtcNow;

                // Log spike incident immediately with top processes
                var topProcs = (await _metricsProvider.GetTopCpuProcessesAsync(opts.TopProcessCount, ct)).ToList();
                var spike = new Incident(
                    UtcNow,
                    IncidentType.CpuSpikeDetected,
                    $"CPU hit {cpuUsage:F1}% (threshold: {opts.ThresholdPercent}%)",
                    topProcs
                );
                _metricsStore.AddIncident(spike);
                await _portalEventPublisher.PublishIncidentAsync(spike, ct);
                _logger.LogWarning("CPU spike detected: {Description}", spike.Description);
            }

            var elapsed = UtcNow - _firstBreachTime.Value;
            if (elapsed.TotalSeconds >= opts.SustainedSeconds)
            {
                await RaiseEmergencyAsync(cpuUsage, opts, ct);
                _firstBreachTime = UtcNow;
            }
        }
        else
        {
            _firstBreachTime = null;
            if (_recovery.Clear(AlertKey.Cpu, UtcNow, _alerts.RecoveryWindow))
                await _alerts.ResolveAsync(AlertKey.Cpu, ct: ct);
        }
    }

    /// <summary>
    /// Reads CPU usage, tolerating an unavailable metric source (NaN or a throwing provider):
    /// the cycle is skipped with a warning on the first miss of a streak instead of failing.
    /// Breach tracking is left untouched so a single missed sample does not reset it.
    /// </summary>
    private bool TryReadCpu(out double cpuUsage)
    {
        try
        {
            cpuUsage = _metricsProvider.GetCpuUsage();
        }
        catch (Exception ex)
        {
            cpuUsage = double.NaN;
            _logger.LogDebug(ex, "Reading CPU usage threw");
        }

        if (double.IsFinite(cpuUsage))
        {
            if (_metricUnavailable)
                _logger.LogInformation("CPU usage is available again");
            _metricUnavailable = false;
            return true;
        }

        if (!_metricUnavailable)
            _logger.LogWarning("CPU usage is unavailable; skipping this cycle");
        _metricUnavailable = true;
        return false;
    }

    /// <summary>
    /// Sustained breach: raise (or keep open) the <c>cpu</c> alert. The alert manager decides
    /// whether anything is recorded or sent; top processes are only sampled when it is.
    /// </summary>
    private Task RaiseEmergencyAsync(double cpuUsage, CpuMonitorOptions opts, CancellationToken ct) =>
        _alerts.RaiseAsync(
            AlertKey.Cpu,
            AlertSeverity.Critical,
            $"CPU Emergency: {cpuUsage:F1}%",
            async token =>
            {
                var topProcesses = (await _metricsProvider.GetTopCpuProcessesAsync(opts.TopProcessCount, token)).ToList();
                return new Incident(
                    UtcNow,
                    IncidentType.CpuThresholdBreached,
                    $"CPU at {cpuUsage:F1}% for over {opts.SustainedSeconds}s",
                    topProcesses
                );
            },
            ct);
}