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
    private readonly ILogger<CpuMonitorWorker> _logger;

    private DateTime? _firstBreachTime;
    private DateTime _lastEmergencyEmail = DateTime.MinValue;

    public CpuMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<CpuMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
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
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CPU Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                var cpuUsage = _metricsProvider.GetCpuUsage();
                var metric = new CpuMetric(DateTime.UtcNow, cpuUsage);
                _metricsStore.AddCpuMetric(metric);

                _logger.LogDebug("CPU: {Usage:F1}%", cpuUsage);

                if (cpuUsage >= opts.ThresholdPercent)
                {
                    if (_firstBreachTime == null)
                    {
                        _firstBreachTime = DateTime.UtcNow;

                        // Log spike incident immediately with top processes
                        var topProcs = _metricsProvider.GetTopCpuProcesses(opts.TopProcessCount).ToList();
                        var spike = new Incident(
                            DateTime.UtcNow,
                            IncidentType.CpuSpikeDetected,
                            $"CPU hit {cpuUsage:F1}% (threshold: {opts.ThresholdPercent}%)",
                            topProcs
                        );
                        _metricsStore.AddIncident(spike);
                        await _portalEventPublisher.PublishIncidentAsync(spike, stoppingToken);
                        _logger.LogWarning("CPU spike detected: {Description}", spike.Description);
                    }

                    var elapsed = DateTime.UtcNow - _firstBreachTime.Value;
                    if (elapsed.TotalSeconds >= opts.SustainedSeconds)
                    {
                        await HandleEmergencyAsync(cpuUsage, opts, stoppingToken);
                        _firstBreachTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    _firstBreachTime = null;
                }

                await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CPU Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CPU Monitor Worker stopped");
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
        if (DateTime.UtcNow - _lastEmergencyEmail < cooldown)
            return;

        var topProcesses = _metricsProvider.GetTopCpuProcesses(opts.TopProcessCount).ToList();
        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.CpuThresholdBreached,
            $"CPU at {cpuUsage:F1}% for over {opts.SustainedSeconds}s",
            topProcesses
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);
        _logger.LogWarning("CPU EMERGENCY: {Description}", incident.Description);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        await _emailService.SendEmailAsync($"[WatchTower] CPU Emergency: {cpuUsage:F1}%", html, ct);

        _lastEmergencyEmail = DateTime.UtcNow;
    }
}
