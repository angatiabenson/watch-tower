using System.Text;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Templates;

namespace WatchTower.Services;

public class ReportBuilder : IReportBuilder
{
    private readonly IMetricsStore _metricsStore;

    public ReportBuilder(IMetricsStore metricsStore)
    {
        _metricsStore = metricsStore;
    }

    public ServerReport BuildReport(TimeSpan period)
    {
        var cpuMetrics = _metricsStore.GetCpuMetrics(period);
        var ramMetrics = _metricsStore.GetRamMetrics(period);
        var diskMetrics = _metricsStore.GetLatestDiskMetrics();
        var diskQueueMetrics = _metricsStore.GetLatestDiskQueueMetrics();
        var networkMetrics = _metricsStore.GetLatestNetworkMetrics();
        var serviceStatuses = _metricsStore.GetLatestServiceStatuses();
        var incidents = _metricsStore.GetIncidents(period);

        var report = new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = period,
            DiskSnapshots = diskMetrics.ToList(),
            DiskQueueSnapshots = diskQueueMetrics.ToList(),
            NetworkSnapshots = networkMetrics.ToList(),
            ServiceSnapshots = serviceStatuses.ToList(),
            Incidents = incidents.ToList()
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
        }

        return report;
    }

    public string RenderScheduledReportHtml(ServerReport report)
    {
        return EmailTemplates.RenderReport(report, emergencyIncident: null);
    }

    public string RenderEmergencyReportHtml(ServerReport report, Incident emergencyIncident)
    {
        return EmailTemplates.RenderReport(report, emergencyIncident);
    }
}
