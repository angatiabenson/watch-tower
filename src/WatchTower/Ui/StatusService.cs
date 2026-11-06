using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Ui;

/// <summary>Builds the dashboard snapshot (<c>GET /api/status</c>).</summary>
public sealed class StatusService(
    AgentInfo agent,
    InstanceIdentity instance,
    IMetricsStore metrics,
    IConfiguration configuration,
    RestartState restartState,
    TimeProvider time,
    IOptionsMonitor<CpuMonitorOptions> cpuOptions,
    IOptionsMonitor<RamMonitorOptions> ramOptions,
    IOptionsMonitor<DiskMonitorOptions> diskOptions,
    IOptionsMonitor<EmailOptions> emailOptions,
    IOptionsMonitor<PortalOptions> portalOptions,
    Updates.UpdateService updates,
    IWorkerHealth workerHealth,
    Services.Alerting.IMaintenanceState maintenance,
    Services.Alerting.IAlertManager alerts,
    Services.SnapshotFreshnessPolicy freshness)
{
    private const int MaxSeriesPoints = 120;

    public object GetStatus()
    {
        var now = time.GetUtcNow();
        var nowUtc = now.UtcDateTime;
        var report = StartupValidator.Validate(configuration);
        var portal = portalOptions.CurrentValue;
        var email = emailOptions.CurrentValue;
        var disk = diskOptions.CurrentValue;

        var cpu = metrics.GetCpuMetrics(TimeSpan.FromHours(1));
        var ram = metrics.GetRamMetrics(TimeSpan.FromHours(1));
        var lastRam = ram.Count > 0 ? ram[^1] : null;

        return new
        {
            agent = new
            {
                version = AgentInfo.Version,
                machineName = agent.MachineName,
                os = agent.OsDescription,
                serviceName = instance.ServiceName,
                isWindowsService = agent.IsWindowsService,
                processId = agent.ProcessId,
                startedAt = agent.StartedAtUtc,
                uptimeSeconds = (long)(now - agent.StartedAtUtc).TotalSeconds,
                installDirectory = agent.Paths.InstallDirectory,
                dataDirectory = agent.Paths.DataDirectory,
            },
            health = new { errors = report.Errors, warnings = report.Warnings },
            restartPending = restartState.Pending,
            maintenance = MaintenanceSummary(maintenance.Current),
            // Alerts currently open (oldest first); they resolve when the condition clears.
            alerts = alerts.GetOpenAlerts().Select(a => new
            {
                key = a.Key,
                severity = Services.Alerting.AlertText.Label(a.Severity),
                title = a.Title,
                description = a.LastDescription,
                openedAt = DateTime.SpecifyKind(a.OpenedAt, DateTimeKind.Utc),
                lastNotifiedAt = a.LastNotifiedAt is { } n ? DateTime.SpecifyKind(n, DateTimeKind.Utc) : (DateTime?)null,
                notifyCount = a.NotifyCount,
            }),
            // Breaches inside their waiting period (oldest first): nothing has been sent; each
            // becomes an alert at alertsAt if it is still present then.
            pendingAlerts = alerts.GetPendingAlerts().Select(p => new
            {
                key = p.Key,
                severity = Services.Alerting.AlertText.Label(p.Severity),
                title = p.Title,
                since = Utc(p.Since),
                alertAfterSeconds = (long)p.AlertAfter.TotalSeconds,
                alertsAt = Utc(p.AlertsAt),
                lastSeenAt = Utc(p.LastSeenAt),
            }),
            // Liveness of the monitoring workers; any stale one makes the heartbeat report failure.
            workers = workerHealth.Snapshot().Select(w => new
            {
                name = w.Worker,
                lastSuccessAt = w.LastSuccessAt,
                intervalSeconds = (long)w.Interval.TotalSeconds,
                stale = w.IsStale,
            }),
            update = UpdateSummary(updates.GetStatus()),
            portal = new { enabled = portal.Enabled, baseUrl = portal.BaseUrl, agentId = portal.AgentId, protocol = portal.Protocol },
            email = new
            {
                configured = !string.IsNullOrWhiteSpace(email.SmtpHost) && email.ToAddresses.Count > 0,
                recipients = email.ToAddresses.Count,
            },
            metrics = new
            {
                cpu = cpu.Count == 0 ? null : new
                {
                    current = Round(cpu[^1].UsagePercent),
                    average = Round(cpu.Average(m => m.UsagePercent)),
                    max = Round(cpu.Max(m => m.UsagePercent)),
                    threshold = cpuOptions.CurrentValue.ThresholdPercent,
                    series = Downsample(cpu.Select(m => (m.Timestamp, m.UsagePercent)).ToList()),
                },
                ram = lastRam is null ? null : new
                {
                    current = Round(lastRam.UsagePercent),
                    average = Round(ram.Average(m => m.UsagePercent)),
                    max = Round(ram.Max(m => m.UsagePercent)),
                    threshold = ramOptions.CurrentValue.ThresholdPercent,
                    totalMb = Math.Round(lastRam.TotalMb),
                    availableMb = Math.Round(lastRam.AvailableMb),
                    series = Downsample(ram.Select(m => (m.Timestamp, m.UsagePercent)).ToList()),
                },
                // Snapshot entries: the last reading (readAt) and whether it is older than its
                // freshness window (the same rule as the metrics batches' isStale).
                disks = metrics.GetLatestDiskMetrics().OrderBy(d => d.DriveName).Select(d => new
                {
                    drive = d.DriveName,
                    totalGb = Round(d.TotalGb),
                    freeGb = Round(d.FreeGb),
                    usagePercent = Round(d.UsagePercent),
                    warningPercent = disk.WarningThresholdPercent,
                    criticalPercent = disk.CriticalThresholdPercent,
                    readAt = Utc(d.Timestamp),
                    isStale = freshness.IsStale(SnapshotKind.Disk, d.DriveName, d.Timestamp, nowUtc),
                }),
                diskQueues = metrics.GetLatestDiskQueueMetrics().Select(q => new
                {
                    instance = q.InstanceName,
                    queueLength = Round(q.QueueLength),
                    readAt = Utc(q.Timestamp),
                    isStale = freshness.IsStale(SnapshotKind.DiskQueue, q.InstanceName, q.Timestamp, nowUtc),
                }),
                network = metrics.GetLatestNetworkMetrics().Select(n => new
                {
                    @interface = n.InterfaceName,
                    outputQueueLength = Round(n.OutputQueueLength),
                    readAt = Utc(n.Timestamp),
                    isStale = freshness.IsStale(SnapshotKind.Network, n.InterfaceName, n.Timestamp, nowUtc),
                }),
                services = metrics.GetLatestServiceStatuses().OrderBy(s => s.DisplayName).Select(s => new
                {
                    name = s.ServiceName,
                    displayName = s.DisplayName,
                    status = s.Status,
                    isRunning = s.IsRunning,
                    readAt = Utc(s.Timestamp),
                    isStale = freshness.IsStale(SnapshotKind.Service, s.ServiceName, s.Timestamp, nowUtc),
                }),
                appPools = metrics.GetLatestApplicationPoolStatuses().OrderBy(p => p.ApplicationPoolName).Select(p => new
                {
                    name = p.ApplicationPoolName,
                    status = p.Status,
                    isRunning = p.IsRunning,
                    readAt = Utc(p.Timestamp),
                    isStale = freshness.IsStale(SnapshotKind.ApplicationPool, p.ApplicationPoolName, p.Timestamp, nowUtc),
                }),
                // Check results only: the checks themselves (Monitoring:Endpoints) are edited in the
                // console's "Endpoint checks" settings section or imported with `WatchTower config import`.
                endpoints = metrics.GetLatestEndpointStatuses().OrderBy(e => e.Name).Select(e => new
                {
                    name = e.Name,
                    type = e.Type,
                    target = e.Target,
                    status = e.Status,
                    isUp = e.IsUp,
                    responseTimeMs = e.ResponseTimeMs,
                    certificateDaysLeft = e.CertificateDaysLeft,
                    detail = e.Detail,
                    checkedAt = DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc),
                    isStale = freshness.IsStale(SnapshotKind.Endpoint, e.Name, e.Timestamp, nowUtc),
                }),
            },
            incidents = metrics.GetIncidents(TimeSpan.FromHours(24))
                .OrderByDescending(i => i.Timestamp)
                .Take(50)
                .Select(i => new
                {
                    timestamp = DateTime.SpecifyKind(i.Timestamp, DateTimeKind.Utc),
                    type = i.Type.ToString(),
                    severity = Severity(i.Type),
                    description = i.Description,
                }),
        };
    }

    public static object MaintenanceSummary(Services.Alerting.MaintenanceWindow window) => new
    {
        active = window.Active,
        until = window.Until,
        reason = window.Reason,
    };

    private static object UpdateSummary(Updates.UpdateStatus status) => new
    {
        phase = status.Phase,
        availableVersion = status.Available?.Version,
        restartAt = status.RestartAt,
        lastResult = status.LastResult,
    };

    public static string Severity(IncidentType type) => type switch
    {
        IncidentType.CpuThresholdBreached or IncidentType.RamThresholdBreached or IncidentType.DiskCritical
            or IncidentType.ServiceDown or IncidentType.ServiceRestartFailed
            or IncidentType.ApplicationPoolDown or IncidentType.ApplicationPoolStartFailed
            or IncidentType.EndpointDown => "critical",
        IncidentType.ServerRestarted => "warning",
        IncidentType.ServiceRestarted or IncidentType.ApplicationPoolStarted or IncidentType.EndpointRecovered => "info",
        _ => "warning",
    };

    private static double Round(double value) => Math.Round(value, 1);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static IEnumerable<object> Downsample(List<(DateTime Timestamp, double Value)> points)
    {
        if (points.Count == 0)
            return [];

        var bucket = (int)Math.Ceiling(points.Count / (double)MaxSeriesPoints);
        return points
            .Select((p, i) => (p, i))
            .GroupBy(x => x.i / bucket)
            .Select(g => new
            {
                t = DateTime.SpecifyKind(g.Last().p.Timestamp, DateTimeKind.Utc),
                v = Round(g.Average(x => x.p.Value)),
            });
    }
}