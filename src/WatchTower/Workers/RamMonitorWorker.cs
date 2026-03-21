using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class RamMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IOptionsMonitor<RamMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly ILogger<RamMonitorWorker> _logger;

    private DateTime? _firstBreachTime;
    private DateTime _lastEmergencyEmail = DateTime.MinValue;

    public RamMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IOptionsMonitor<RamMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        ILogger<RamMonitorWorker> logger
    )
    {
        _metricsProvider = metricsProvider;
        _metricsStore = metricsStore;
        _emailService = emailService;
        _reportBuilder = reportBuilder;
        _options = options;
        _reportingOptions = reportingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RAM Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
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
                        _firstBreachTime = DateTime.UtcNow;

                        // Log spike incident immediately with top processes
                        var topProcs = _metricsProvider.GetTopMemoryProcesses(3).ToList();
                        var spike = new Incident(
                            DateTime.UtcNow,
                            IncidentType.RamSpikeDetected,
                            $"RAM hit {ramMetric.UsagePercent:F1}% ({ramMetric.AvailableMb:F0} MB available, threshold: {opts.ThresholdPercent}%)",
                            topProcs
                        );
                        _metricsStore.AddIncident(spike);
                        _logger.LogWarning("RAM spike detected: {Description}", spike.Description);
                    }

                    var elapsed = DateTime.UtcNow - _firstBreachTime.Value;
                    if (elapsed.TotalSeconds >= opts.SustainedSeconds)
                    {
                        await HandleEmergencyAsync(ramMetric, opts, stoppingToken);
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
                _logger.LogError(ex, "Error in RAM Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("RAM Monitor Worker stopped");
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
        if (DateTime.UtcNow - _lastEmergencyEmail < cooldown)
            return;

        var topProcesses = _metricsProvider.GetTopMemoryProcesses(opts.TopProcessCount).ToList();
        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.RamThresholdBreached,
            $"RAM at {ramMetric.UsagePercent:F1}% ({ramMetric.AvailableMb:F0} MB available) for over {opts.SustainedSeconds}s",
            topProcesses
        );

        _metricsStore.AddIncident(incident);
        _logger.LogWarning("RAM EMERGENCY: {Description}", incident.Description);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        await _emailService.SendEmailAsync(
            $"[WatchTower] RAM Emergency: {ramMetric.UsagePercent:F1}%",
            html,
            ct
        );

        _lastEmergencyEmail = DateTime.UtcNow;
    }
}
