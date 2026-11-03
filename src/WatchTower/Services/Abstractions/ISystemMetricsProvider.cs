using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface ISystemMetricsProvider : IDisposable
{
    float GetCpuUsage();
    RamMetric GetRamMetric();
    IReadOnlyList<DiskMetric> GetDiskMetrics();
    IReadOnlyList<DiskQueueMetric> GetDiskQueueMetrics();
    IReadOnlyList<NetworkMetric> GetNetworkMetrics();

    /// <summary>Fixed drives that exist (ready or not). Throws when they cannot be enumerated.</summary>
    ResourceInventory GetDiskInventory();

    /// <summary><c>PhysicalDisk</c> counter instances (without <c>_Total</c>). Throws when they cannot be enumerated.</summary>
    ResourceInventory GetDiskQueueInventory();

    /// <summary><c>Network Interface</c> counter instances. Throws when they cannot be enumerated.</summary>
    ResourceInventory GetNetworkInventory();
    Task<IReadOnlyList<ProcessInfo>> GetTopCpuProcessesAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessInfo>> GetTopMemoryProcessesAsync(int count, CancellationToken ct = default);
}