using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface IMetricsStore
{
    void AddCpuMetric(CpuMetric metric);
    void AddRamMetric(RamMetric metric);
    void AddDiskMetric(DiskMetric metric);
    void AddServiceStatus(ServiceStatus status);
    void AddIncident(Incident incident);

    IReadOnlyList<CpuMetric> GetCpuMetrics(TimeSpan period);
    IReadOnlyList<RamMetric> GetRamMetrics(TimeSpan period);
    IReadOnlyList<DiskMetric> GetLatestDiskMetrics();
    IReadOnlyList<ServiceStatus> GetLatestServiceStatuses();
    IReadOnlyList<Incident> GetIncidents(TimeSpan period);

    void Purge(TimeSpan maxAge);
}
