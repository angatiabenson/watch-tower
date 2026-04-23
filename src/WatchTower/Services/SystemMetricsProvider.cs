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

    public IReadOnlyList<ProcessInfo> GetTopCpuProcesses(int count)
    {
        try
        {
            return SampleProcesses()
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

    public IReadOnlyList<ProcessInfo> GetTopMemoryProcesses(int count)
    {
        try
        {
            return CaptureProcessSamples()
                .OrderByDescending(p => p!.MemoryMb)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get top memory processes");
            return Array.Empty<ProcessInfo>();
        }
    }

    private IReadOnlyList<ProcessInfo> SampleProcesses()
    {
        var initialSamples = CaptureProcessSamples()
            .ToDictionary(p => p.Pid, p => p);

        var startedAt = DateTime.UtcNow;
        Thread.Sleep(ProcessCpuSampleMilliseconds);
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

    private double GetTotalPhysicalMemoryMb()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0);
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
