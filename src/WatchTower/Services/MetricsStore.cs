using System.Collections.Concurrent;

using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class MetricsStore : IMetricsStore
{
    private readonly ConcurrentQueue<CpuMetric> _cpuMetrics = new();
    private readonly ConcurrentQueue<RamMetric> _ramMetrics = new();
    private readonly ConcurrentDictionary<string, DiskMetric> _diskMetrics = new();
    private readonly ConcurrentDictionary<string, DiskQueueMetric> _diskQueueMetrics = new();
    private readonly ConcurrentDictionary<string, NetworkMetric> _networkMetrics = new();
    private readonly ConcurrentDictionary<string, ServiceStatus> _serviceStatuses = new();
    private readonly ConcurrentDictionary<string, ApplicationPoolStatus> _applicationPoolStatuses =
        new();
    private readonly ConcurrentDictionary<string, EndpointStatus> _endpointStatuses = new();
    private readonly ConcurrentQueue<Incident> _incidents = new();

    public void AddCpuMetric(CpuMetric metric) => _cpuMetrics.Enqueue(metric);

    public void AddRamMetric(RamMetric metric) => _ramMetrics.Enqueue(metric);

    public void AddDiskMetric(DiskMetric metric) => _diskMetrics[metric.DriveName] = metric;

    public void AddDiskQueueMetric(DiskQueueMetric metric) =>
        _diskQueueMetrics[metric.InstanceName] = metric;

    public void AddNetworkMetric(NetworkMetric metric) =>
        _networkMetrics[metric.InterfaceName] = metric;

    public void AddServiceStatus(ServiceStatus status) =>
        _serviceStatuses[status.ServiceName] = status;

    public void AddApplicationPoolStatus(ApplicationPoolStatus status) =>
        _applicationPoolStatuses[status.ApplicationPoolName] = status;

    public void AddEndpointStatus(EndpointStatus status) => _endpointStatuses[status.Name] = status;

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

    public IReadOnlyList<DiskQueueMetric> GetLatestDiskQueueMetrics() =>
        _diskQueueMetrics.Values.ToList();

    public IReadOnlyList<NetworkMetric> GetLatestNetworkMetrics() =>
        _networkMetrics.Values.ToList();

    public IReadOnlyList<ServiceStatus> GetLatestServiceStatuses() =>
        _serviceStatuses.Values.ToList();

    public IReadOnlyList<ApplicationPoolStatus> GetLatestApplicationPoolStatuses() =>
        _applicationPoolStatuses.Values.ToList();

    public IReadOnlyList<EndpointStatus> GetLatestEndpointStatuses() => _endpointStatuses.Values.ToList();

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

    public PurgeReportResult PurgeReportedData(long cpuForwardedRowId, long ramForwardedRowId, DateTime incidentsUpToUtc)
    {
        // In-memory cursors are running indices; clear everything that has been
        // collected. Snapshots are always cleared; incidents only up to the report cutoff.
        _cpuMetrics.Clear();
        _ramMetrics.Clear();
        _diskMetrics.Clear();
        _diskQueueMetrics.Clear();
        _networkMetrics.Clear();
        _serviceStatuses.Clear();
        _applicationPoolStatuses.Clear();
        _endpointStatuses.Clear();

        var newer = new List<Incident>();
        while (_incidents.TryDequeue(out var incident))
        {
            if (incident.Timestamp > incidentsUpToUtc)
                newer.Add(incident);
        }
        foreach (var incident in newer)
            _incidents.Enqueue(incident);

        return new PurgeReportResult(CpuTableEmptied: true, RamTableEmptied: true);
    }

    public (long LastRowId, IReadOnlyList<CpuMetric> Metrics) GetCpuMetricsSince(long afterRowId, int limit)
    {
        var all = _cpuMetrics.ToArray();
        var slice = all.Skip((int)afterRowId).Take(limit).ToList();
        var lastRowId = afterRowId + slice.Count;
        return (lastRowId, slice);
    }

    public (long LastRowId, IReadOnlyList<RamMetric> Metrics) GetRamMetricsSince(long afterRowId, int limit)
    {
        var all = _ramMetrics.ToArray();
        var slice = all.Skip((int)afterRowId).Take(limit).ToList();
        var lastRowId = afterRowId + slice.Count;
        return (lastRowId, slice);
    }
}