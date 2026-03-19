using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface ISystemMetricsProvider : IDisposable
{
    float GetCpuUsage();
    RamMetric GetRamMetric();
    IReadOnlyList<DiskMetric> GetDiskMetrics();
    IReadOnlyList<DiskQueueMetric> GetDiskQueueMetrics();
    IReadOnlyList<NetworkMetric> GetNetworkMetrics();
    IReadOnlyList<ProcessInfo> GetTopCpuProcesses(int count);
    IReadOnlyList<ProcessInfo> GetTopMemoryProcesses(int count);
}
