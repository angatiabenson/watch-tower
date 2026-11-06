using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Templates;

namespace WatchTower.Services;

public class ReportBuilder : IReportBuilder
{
    private readonly IMetricsStore _metricsStore;
    private readonly IOptionsMonitor<DisplayOptions> _displayOptions;

    private readonly Alerting.PendingAlertTracker? _waits;

    public ReportBuilder(IMetricsStore metricsStore, IOptionsMonitor<DisplayOptions> displayOptions,
        Alerting.PendingAlertTracker? waits = null)
    {
        _metricsStore = metricsStore;
        _displayOptions = displayOptions;
        _waits = waits;
    }

    private (TimeZoneInfo Zone, string Label) ResolveZone()
    {
        var display = _displayOptions.CurrentValue;
        try { return (TimeZoneInfo.FindSystemTimeZoneById(display.Timezone), display.TimezoneLabel); }
        catch { return (TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time"), display.TimezoneLabel); }
    }

    public ServerReport BuildReport(TimeSpan period)
    {
        // Captured before any data is read: GeneratedAt is the report's snapshot cutoff, and the
        // post-report purge deletes incidents only up to it. Taking it first means an incident
        // racing the read can at worst be reported twice, never purged without being reported.
        var generatedAt = DateTime.UtcNow;

        var cpuMetrics = _metricsStore.GetCpuMetrics(period);
        var ramMetrics = _metricsStore.GetRamMetrics(period);
        var diskMetrics = _metricsStore.GetLatestDiskMetrics();
        var diskQueueMetrics = _metricsStore.GetLatestDiskQueueMetrics();
        var networkMetrics = _metricsStore.GetLatestNetworkMetrics();
        var serviceStatuses = _metricsStore.GetLatestServiceStatuses();
        var applicationPoolStatuses = _metricsStore.GetLatestApplicationPoolStatuses();
        var incidents = _metricsStore.GetIncidents(period);

        var report = new ServerReport
        {
            GeneratedAt = generatedAt,
            ReportPeriod = period,
            DiskSnapshots = diskMetrics.ToList(),
            DiskQueueSnapshots = diskQueueMetrics.ToList(),
            NetworkSnapshots = networkMetrics.ToList(),
            ServiceSnapshots = serviceStatuses.ToList(),
            ApplicationPoolSnapshots = applicationPoolStatuses.ToList(),
            Incidents = incidents.ToList(),
            // Breaches that cleared inside their waiting period: nobody was notified, but the
            // report shows the agent kept watching. In memory only (since the agent started).
            ShortBreaches = _waits?.ShortBreaches(generatedAt - period).ToList() ?? [],
        };

        if (cpuMetrics.Count > 0)
        {
            report.CpuMin = cpuMetrics.Min(m => m.UsagePercent);
            report.CpuAvg = cpuMetrics.Average(m => m.UsagePercent);
            report.CpuMax = cpuMetrics.Max(m => m.UsagePercent);
        }

        if (ramMetrics.Count > 0)
        {
            report.RamMin = ramMetrics.Min(m => m.UsagePercent);
            report.RamAvg = ramMetrics.Average(m => m.UsagePercent);
            report.RamMax = ramMetrics.Max(m => m.UsagePercent);

            var latestRam = ramMetrics.OrderByDescending(m => m.Timestamp).First();
            report.RamTotalGb = latestRam.TotalMb / 1024.0;
            report.RamUsedGb = (latestRam.TotalMb - latestRam.AvailableMb) / 1024.0;
        }

        return report;
    }

    public string RenderScheduledReportHtml(ServerReport report)
    {
        var (zone, label) = ResolveZone();
        return EmailTemplates.RenderReport(report, emergencyIncident: null, zone, label);
    }

    public string RenderEmergencyReportHtml(ServerReport report, Incident emergencyIncident)
    {
        var (zone, label) = ResolveZone();
        return EmailTemplates.RenderReport(report, emergencyIncident, zone, label);
    }

    public string RenderAlertHtml(Alerting.AlertNotification notification)
    {
        var (zone, label) = ResolveZone();
        return EmailTemplates.RenderAlert(notification, zone, label);
    }
}