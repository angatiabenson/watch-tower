using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class DiskMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IOptionsMonitor<DiskMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly ILogger<DiskMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerDrive = new();

    public DiskMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IOptionsMonitor<DiskMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        ILogger<DiskMonitorWorker> logger)
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
        _logger.LogInformation("Disk Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                var diskMetrics = _metricsProvider.GetDiskMetrics();

                foreach (var disk in diskMetrics)
                {
                    _metricsStore.AddDiskMetric(disk);

                    if (disk.UsagePercent >= opts.CriticalThresholdPercent)
                    {
                        _logger.LogWarning("DISK CRITICAL: {Drive} at {Usage:F1}%", disk.DriveName, disk.UsagePercent);
                        await HandleEmergencyAsync(disk, stoppingToken);
                    }
                    else if (disk.UsagePercent >= opts.WarningThresholdPercent)
                    {
                        _logger.LogWarning("DISK WARNING: {Drive} at {Usage:F1}%", disk.DriveName, disk.UsagePercent);
                    }
                    else
                    {
                        _logger.LogDebug("Disk {Drive}: {Usage:F1}% ({Free:F1} GB free)", disk.DriveName, disk.UsagePercent, disk.FreeGb);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Disk Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Disk Monitor Worker stopped");
    }

    private async Task HandleEmergencyAsync(DiskMetric disk, CancellationToken ct)
    {
        var cooldown = TimeSpan.FromSeconds(_reportingOptions.CurrentValue.EmergencyCooldownSeconds);
        var lastEmail = _lastEmergencyPerDrive.GetValueOrDefault(disk.DriveName, DateTime.MinValue);

        if (DateTime.UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.DiskCritical,
            $"Drive {disk.DriveName} at {disk.UsagePercent:F1}% ({disk.FreeGb:F1} GB free of {disk.TotalGb:F1} GB)");

        _metricsStore.AddIncident(incident);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(2.5));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        await _emailService.SendEmailAsync($"[WatchTower] Disk Critical: {disk.DriveName} at {disk.UsagePercent:F1}%", html, ct);

        _lastEmergencyPerDrive[disk.DriveName] = DateTime.UtcNow;
    }
}
