using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class CpuMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<CpuMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CpuMonitorWorker> _logger;

    private DateTime? _firstBreachTime;
    private DateTime _lastEmergencyEmail = DateTime.MinValue;
    private bool _metricUnavailable;

    public CpuMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<CpuMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        TimeProvider timeProvider,
        ILogger<CpuMonitorWorker> logger
    )
    {
        _metricsProvider = metricsProvider;
        _metricsStore = metricsStore;
        _emailService = emailService;
        _reportBuilder = reportBuilder;
        _portalEventPublisher = portalEventPublisher;
        _options = options;
        _reportingOptions = reportingOptions;
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
                await HandleEmergencyAsync(cpuUsage, opts, ct);
                _firstBreachTime = UtcNow;
            }
        }
        else
        {
            _firstBreachTime = null;
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

    private async Task HandleEmergencyAsync(
        double cpuUsage,
        CpuMonitorOptions opts,
        CancellationToken ct
    )
    {
        var cooldown = TimeSpan.FromSeconds(
            _reportingOptions.CurrentValue.EmergencyCooldownSeconds
        );
        if (UtcNow - _lastEmergencyEmail < cooldown)
            return;

        var topProcesses = (await _metricsProvider.GetTopCpuProcessesAsync(opts.TopProcessCount, ct)).ToList();
        var incident = new Incident(
            UtcNow,
            IncidentType.CpuThresholdBreached,
            $"CPU at {cpuUsage:F1}% for over {opts.SustainedSeconds}s",
            topProcesses
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);
        _logger.LogWarning("CPU EMERGENCY: {Description}", incident.Description);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        var sent = await _emailService.SendEmailAsync($"[WatchTower] CPU Emergency: {cpuUsage:F1}%", html, ct);

        // Only start the cooldown if the alert was actually delivered, so a transient
        // SMTP outage does not silently suppress alerts for the whole cooldown window.
        if (sent)
            _lastEmergencyEmail = UtcNow;
    }
}