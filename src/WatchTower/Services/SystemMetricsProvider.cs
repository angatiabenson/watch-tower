using System.Diagnostics;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class SystemMetricsProvider : ISystemMetricsProvider
{
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
        var usagePercent = totalMb > 0 ? ((totalMb - availableMb) / totalMb) * 100.0 : 0;

        return new RamMetric(DateTime.UtcNow, usagePercent, availableMb, totalMb);
    }

    public IReadOnlyList<DiskMetric> GetDiskMetrics()
    {
        var metrics = new List<DiskMetric>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var freeGb = drive.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var usagePercent = totalGb > 0 ? ((totalGb - freeGb) / totalGb) * 100.0 : 0;

            metrics.Add(new DiskMetric(DateTime.UtcNow, drive.Name, totalGb, freeGb, usagePercent));
        }
        return metrics;
    }

    public IReadOnlyList<ProcessInfo> GetTopCpuProcesses(int count)
    {
        try
        {
            return Process.GetProcesses()
                .Where(p => p.Id != 0)
                .Select(p =>
                {
                    try
                    {
                        return new ProcessInfo(p.ProcessName, p.Id, p.TotalProcessorTime.TotalSeconds, p.WorkingSet64 / (1024.0 * 1024.0));
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(p => p != null)
                .OrderByDescending(p => p!.CpuSeconds)
                .Take(count)
                .ToList()!;
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
            return Process.GetProcesses()
                .Where(p => p.Id != 0)
                .Select(p =>
                {
                    try
                    {
                        return new ProcessInfo(p.ProcessName, p.Id, p.TotalProcessorTime.TotalSeconds, p.WorkingSet64 / (1024.0 * 1024.0));
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(p => p != null)
                .OrderByDescending(p => p!.MemoryMb)
                .Take(count)
                .ToList()!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get top memory processes");
            return Array.Empty<ProcessInfo>();
        }
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
