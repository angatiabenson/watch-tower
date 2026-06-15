using System.Diagnostics;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class SystemMetricsProvider : ISystemMetricsProvider
{
    private const int ProcessCpuSampleMilliseconds = 500;

    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private readonly ILogger<SystemMetricsProvider> _logger;

    public SystemMetricsProvider(ILogger<SystemMetricsProvider> logger)
    {
        _logger = logger;

        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");

        // First call always returns 0, discard it
        _cpuCounter.NextValue();
        _ramCounter.NextValue();
    }

    public float GetCpuUsage() => _cpuCounter.NextValue();

    public RamMetric GetRamMetric()
    {
        var availableMb = _ramCounter.NextValue();
        var totalMb = GetTotalPhysicalMemoryMb();
        var usagePercent = totalMb > 0 ? (totalMb - availableMb) / totalMb * 100.0 : 0;

        return new RamMetric(DateTime.UtcNow, usagePercent, availableMb, totalMb);
    }

    public IReadOnlyList<DiskMetric> GetDiskMetrics()
    {
        var metrics = new List<DiskMetric>();
        foreach (
            var drive in DriveInfo
                .GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        )
        {
            var totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var freeGb = drive.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var usagePercent = totalGb > 0 ? ((totalGb - freeGb) / totalGb) * 100.0 : 0;

            metrics.Add(new DiskMetric(DateTime.UtcNow, drive.Name, totalGb, freeGb, usagePercent));
        }
        return metrics;
    }

    public IReadOnlyList<DiskQueueMetric> GetDiskQueueMetrics()
    {
        var metrics = new List<DiskQueueMetric>();
        try
        {
            var category = new PerformanceCounterCategory("PhysicalDisk");
            var instances = category.GetInstanceNames().Where(i => i != "_Total");

            foreach (var instance in instances)
            {
                try
                {
                    using var counter = new PerformanceCounter(
                        "PhysicalDisk",
                        "Current Disk Queue Length",
                        instance
                    );
                    var queueLength = counter.NextValue();
                    metrics.Add(new DiskQueueMetric(DateTime.UtcNow, instance, queueLength));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Failed to read disk queue for instance: {Instance}",
                        instance
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate PhysicalDisk performance counters");
        }
        return metrics;
    }

    public IReadOnlyList<NetworkMetric> GetNetworkMetrics()
    {
        var metrics = new List<NetworkMetric>();
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            var instances = category.GetInstanceNames();

            foreach (var instance in instances)
            {
                try
                {
                    using var counter = new PerformanceCounter(
                        "Network Interface",
                        "Output Queue Length",
                        instance
                    );
                    var queueLength = counter.NextValue();
                    metrics.Add(new NetworkMetric(DateTime.UtcNow, instance, queueLength));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Failed to read network queue for interface: {Interface}",
                        instance
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Network Interface performance counters");
        }
        return metrics;
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetTopCpuProcessesAsync(int count, CancellationToken ct = default)
    {
        try
        {
            return (await SampleProcessesAsync(ct))
                .OrderByDescending(p => p.CpuPercent)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get top CPU processes");
            return Array.Empty<ProcessInfo>();
        }
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetTopMemoryProcessesAsync(int count, CancellationToken ct = default)
    {
        try
        {
            return (await SampleProcessesAsync(ct))
                .OrderByDescending(p => p.MemoryMb)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get top memory processes");
            return Array.Empty<ProcessInfo>();
        }
    }

    private async Task<IReadOnlyList<ProcessInfo>> SampleProcessesAsync(CancellationToken ct)
    {
        var initialSamples = CaptureProcessSamples()
            .ToDictionary(p => p.Pid, p => p);

        var startedAt = DateTime.UtcNow;
        await Task.Delay(ProcessCpuSampleMilliseconds, ct);
        var elapsedSeconds = Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001);

        var processorCount = Math.Max(Environment.ProcessorCount, 1);
        var results = new List<ProcessInfo>();

        foreach (var current in CaptureProcessSamples())
        {
            if (!initialSamples.TryGetValue(current.Pid, out var initial))
                continue;

            var cpuDeltaSeconds = current.CpuSeconds - initial.CpuSeconds;
            var cpuPercent = cpuDeltaSeconds / elapsedSeconds / processorCount * 100.0;
            cpuPercent = Math.Clamp(cpuPercent, 0, 100);

            results.Add(current with { CpuPercent = cpuPercent });
        }

        return results;
    }

    private static IReadOnlyList<ProcessInfo> CaptureProcessSamples()
    {
        var samples = new List<ProcessInfo>();

        foreach (var process in Process.GetProcesses().Where(p => p.Id != 0))
        {
            using (process)
            {
                try
                {
                    samples.Add(
                        new ProcessInfo(
                            process.ProcessName,
                            process.Id,
                            process.TotalProcessorTime.TotalSeconds,
                            process.WorkingSet64 / (1024.0 * 1024.0)
                        )
                    );
                }
                catch
                {
                    // Processes can exit or deny access while being sampled.
                }
            }
        }

        return samples;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);

    private double GetTotalPhysicalMemoryMb()
    {
        try
        {
            // Use the machine-wide installed physical RAM (GlobalMemoryStatusEx) so the
            // percentage is correct on memory-limited/containerised hosts. The previous
            // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is process/GC-scoped and does
            // not match the machine-scoped "Available MBytes" counter used for availableMb.
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status) && status.ullTotalPhys > 0)
                return status.ullTotalPhys / (1024.0 * 1024.0);

            _logger.LogWarning("GlobalMemoryStatusEx failed; falling back to GC memory info");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query physical memory via GlobalMemoryStatusEx");
        }

        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get total physical memory");
            return 0;
        }
    }

    public void Dispose()
    {
        _cpuCounter.Dispose();
        _ramCounter.Dispose();
    }
}
