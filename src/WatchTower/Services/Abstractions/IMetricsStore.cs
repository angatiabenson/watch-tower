using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface IMetricsStore
{
    void AddCpuMetric(CpuMetric metric);
    void AddRamMetric(RamMetric metric);
    void AddDiskMetric(DiskMetric metric);
    void AddDiskQueueMetric(DiskQueueMetric metric);
    void AddNetworkMetric(NetworkMetric metric);
    void AddServiceStatus(ServiceStatus status);
    void AddIncident(Incident incident);

    IReadOnlyList<CpuMetric> GetCpuMetrics(TimeSpan period);
    IReadOnlyList<RamMetric> GetRamMetrics(TimeSpan period);
    IReadOnlyList<DiskMetric> GetLatestDiskMetrics();
    IReadOnlyList<DiskQueueMetric> GetLatestDiskQueueMetrics();
    IReadOnlyList<NetworkMetric> GetLatestNetworkMetrics();
    IReadOnlyList<ServiceStatus> GetLatestServiceStatuses();
    IReadOnlyList<Incident> GetIncidents(TimeSpan period);

    void Purge(TimeSpan maxAge);
}
