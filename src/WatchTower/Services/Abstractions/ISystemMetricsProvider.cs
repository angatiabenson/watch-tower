using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface ISystemMetricsProvider : IDisposable
{
    float GetCpuUsage();
    RamMetric GetRamMetric();
    IReadOnlyList<DiskMetric> GetDiskMetrics();
    IReadOnlyList<DiskQueueMetric> GetDiskQueueMetrics();
    IReadOnlyList<NetworkMetric> GetNetworkMetrics();
    Task<IReadOnlyList<ProcessInfo>> GetTopCpuProcessesAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessInfo>> GetTopMemoryProcessesAsync(int count, CancellationToken ct = default);
}
