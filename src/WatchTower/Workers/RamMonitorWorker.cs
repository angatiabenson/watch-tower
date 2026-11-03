using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class RamMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<RamMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RamMonitorWorker> _logger;

    private DateTime? _firstBreachTime;
    private DateTime _lastEmergencyEmail = DateTime.MinValue;

    public RamMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<RamMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        TimeProvider timeProvider,
        ILogger<RamMonitorWorker> logger
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
        _logger.LogInformation("RAM Monitor Worker started");

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
                _logger.LogError(ex, "Error in RAM Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("RAM Monitor Worker stopped");
    }

    /// <summary>Runs a single poll/evaluate cycle. Exposed for deterministic testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var ramMetric = _metricsProvider.GetRamMetric();
        _metricsStore.AddRamMetric(ramMetric);

        _logger.LogDebug(
            "RAM: {Usage:F1}% ({Available:F0} MB available / {Total:F0} MB total)",
            ramMetric.UsagePercent,
            ramMetric.AvailableMb,
            ramMetric.TotalMb
        );

        if (ramMetric.UsagePercent >= opts.ThresholdPercent)
        {
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
                await HandleEmergencyAsync(ramMetric, opts, ct);
                _firstBreachTime = UtcNow;
            }
        }
        else
        {
            _firstBreachTime = null;
        }
    }

    private async Task HandleEmergencyAsync(
        RamMetric ramMetric,
        RamMonitorOptions opts,
        CancellationToken ct
    )
    {
        var cooldown = TimeSpan.FromSeconds(
            _reportingOptions.CurrentValue.EmergencyCooldownSeconds
        );
        if (UtcNow - _lastEmergencyEmail < cooldown)
            return;

        var topProcesses = (await _metricsProvider.GetTopMemoryProcessesAsync(opts.TopProcessCount, ct)).ToList();
        var incident = new Incident(
            UtcNow,
            IncidentType.RamThresholdBreached,
            $"RAM at {ramMetric.UsagePercent:F1}% ({ramMetric.AvailableMb:F0} MB available) for over {opts.SustainedSeconds}s",
            topProcesses
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);
        _logger.LogWarning("RAM EMERGENCY: {Description}", incident.Description);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        var sent = await _emailService.SendEmailAsync(
            $"[WatchTower] RAM Emergency: {ramMetric.UsagePercent:F1}%",
            html,
            ct
        );

        // Only start the cooldown if the alert was actually delivered, so a transient
        // SMTP outage does not silently suppress alerts for the whole cooldown window.
        if (sent)
            _lastEmergencyEmail = UtcNow;
    }
}