using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class DiskMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<DiskMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly ILogger<DiskMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerDrive = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastQueueEmergencyPerDisk = new();

    public DiskMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<DiskMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        ILogger<DiskMonitorWorker> logger
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
                        _logger.LogWarning(
                            "DISK CRITICAL: {Drive} at {Usage:F1}%",
                            disk.DriveName,
                            disk.UsagePercent
                        );
                        await HandleEmergencyAsync(disk, stoppingToken);
                    }
                    else if (disk.UsagePercent >= opts.WarningThresholdPercent)
                    {
                        _logger.LogWarning(
                            "DISK WARNING: {Drive} at {Usage:F1}%",
                            disk.DriveName,
                            disk.UsagePercent
                        );
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Disk {Drive}: {Usage:F1}% ({Free:F1} GB free)",
                            disk.DriveName,
                            disk.UsagePercent,
                            disk.FreeGb
                        );
                    }
                }

                // Disk queue length monitoring
                var queueMetrics = _metricsProvider.GetDiskQueueMetrics();
                foreach (var queue in queueMetrics)
                {
                    _metricsStore.AddDiskQueueMetric(queue);

                    if (queue.QueueLength > opts.QueueLengthWarningThreshold)
                    {
                        _logger.LogWarning(
                            "DISK QUEUE HIGH: {Disk} queue length {Queue:F1} (threshold: {Threshold})",
                            queue.InstanceName,
                            queue.QueueLength,
                            opts.QueueLengthWarningThreshold
                        );
                        await HandleQueueEmergencyAsync(queue, stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Disk queue {Disk}: {Queue:F1}",
                            queue.InstanceName,
                            queue.QueueLength
                        );
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
        var cooldown = TimeSpan.FromSeconds(
            _reportingOptions.CurrentValue.EmergencyCooldownSeconds
        );
        var lastEmail = _lastEmergencyPerDrive.GetValueOrDefault(disk.DriveName, DateTime.MinValue);

        if (DateTime.UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.DiskCritical,
            $"Drive {disk.DriveName} at {disk.UsagePercent:F1}% ({disk.FreeGb:F1} GB free of {disk.TotalGb:F1} GB)"
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        var sent = await _emailService.SendEmailAsync(
            $"[WatchTower] Disk Critical: {disk.DriveName} at {disk.UsagePercent:F1}%",
            html,
            ct
        );

        if (sent)
            _lastEmergencyPerDrive[disk.DriveName] = DateTime.UtcNow;
    }

    private async Task HandleQueueEmergencyAsync(DiskQueueMetric queue, CancellationToken ct)
    {
        var cooldown = TimeSpan.FromSeconds(
            _reportingOptions.CurrentValue.EmergencyCooldownSeconds
        );
        var lastEmail = _lastQueueEmergencyPerDisk.GetValueOrDefault(
            queue.InstanceName,
            DateTime.MinValue
        );

        if (DateTime.UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.DiskQueueHigh,
            $"Disk '{queue.InstanceName}' queue length at {queue.QueueLength:F1} (threshold: {_options.CurrentValue.QueueLengthWarningThreshold})"
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        var sent = await _emailService.SendEmailAsync(
            $"[WatchTower] Disk Queue High: {queue.InstanceName} at {queue.QueueLength:F1}",
            html,
            ct
        );

        if (sent)
            _lastQueueEmergencyPerDisk[queue.InstanceName] = DateTime.UtcNow;
    }
}
