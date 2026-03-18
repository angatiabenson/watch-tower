using System.Collections.Concurrent;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class MetricsStore : IMetricsStore
{
    private readonly ConcurrentQueue<CpuMetric> _cpuMetrics = new();
    private readonly ConcurrentQueue<RamMetric> _ramMetrics = new();
    private readonly ConcurrentDictionary<string, DiskMetric> _diskMetrics = new();
    private readonly ConcurrentDictionary<string, ServiceStatus> _serviceStatuses = new();
    private readonly ConcurrentQueue<Incident> _incidents = new();

    public void AddCpuMetric(CpuMetric metric) => _cpuMetrics.Enqueue(metric);

    public void AddRamMetric(RamMetric metric) => _ramMetrics.Enqueue(metric);

    public void AddDiskMetric(DiskMetric metric) => _diskMetrics[metric.DriveName] = metric;

    public void AddServiceStatus(ServiceStatus status) => _serviceStatuses[status.ServiceName] = status;

    public void AddIncident(Incident incident) => _incidents.Enqueue(incident);

    public IReadOnlyList<CpuMetric> GetCpuMetrics(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        return _cpuMetrics.Where(m => m.Timestamp >= cutoff).ToList();
    }

    public IReadOnlyList<RamMetric> GetRamMetrics(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        return _ramMetrics.Where(m => m.Timestamp >= cutoff).ToList();
    }

    public IReadOnlyList<DiskMetric> GetLatestDiskMetrics() => _diskMetrics.Values.ToList();

    public IReadOnlyList<ServiceStatus> GetLatestServiceStatuses() => _serviceStatuses.Values.ToList();

    public IReadOnlyList<Incident> GetIncidents(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        return _incidents.Where(i => i.Timestamp >= cutoff).ToList();
    }

    public void Purge(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        while (_cpuMetrics.TryPeek(out var cpu) && cpu.Timestamp < cutoff)
            _cpuMetrics.TryDequeue(out _);

        while (_ramMetrics.TryPeek(out var ram) && ram.Timestamp < cutoff)
            _ramMetrics.TryDequeue(out _);

        while (_incidents.TryPeek(out var incident) && incident.Timestamp < cutoff)
            _incidents.TryDequeue(out _);
    }
}
