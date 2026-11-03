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
    void AddApplicationPoolStatus(ApplicationPoolStatus status);
    void AddEndpointStatus(EndpointStatus status);
    void AddIncident(Incident incident);

    IReadOnlyList<CpuMetric> GetCpuMetrics(TimeSpan period);
    IReadOnlyList<RamMetric> GetRamMetrics(TimeSpan period);
    IReadOnlyList<DiskMetric> GetLatestDiskMetrics();
    IReadOnlyList<DiskQueueMetric> GetLatestDiskQueueMetrics();
    IReadOnlyList<NetworkMetric> GetLatestNetworkMetrics();
    IReadOnlyList<ServiceStatus> GetLatestServiceStatuses();
    IReadOnlyList<ApplicationPoolStatus> GetLatestApplicationPoolStatuses();
    IReadOnlyList<EndpointStatus> GetLatestEndpointStatuses();
    IReadOnlyList<Incident> GetIncidents(TimeSpan period);

    /// <summary>Age-based safety-net purge: deletes cpu/ram/incident rows older than <paramref name="maxAge"/>.</summary>
    void Purge(TimeSpan maxAge);

    /// <summary>
    /// Post-report purge. Deletes cpu/ram rows only up to the rowids that have been
    /// confirmed delivered to the portal (so un-acked rows are never lost), clears all
    /// snapshot tables, and deletes incidents with a timestamp at or before
    /// <paramref name="incidentsUpToUtc"/> (the report's snapshot time) so incidents raised
    /// while the report was being built and sent survive for the next report. Returns which
    /// append tables became empty so the caller can reset the forwarding cursors (SQLite
    /// reuses rowids from 1 once a table is emptied).
    /// </summary>
    PurgeReportResult PurgeReportedData(long cpuForwardedRowId, long ramForwardedRowId, DateTime incidentsUpToUtc);

    (long LastRowId, IReadOnlyList<CpuMetric> Metrics) GetCpuMetricsSince(long afterRowId, int limit);
    (long LastRowId, IReadOnlyList<RamMetric> Metrics) GetRamMetricsSince(long afterRowId, int limit);
}

/// <summary>Outcome of <see cref="IMetricsStore.PurgeReportedData"/>.</summary>
public record PurgeReportResult(bool CpuTableEmptied, bool RamTableEmptied);