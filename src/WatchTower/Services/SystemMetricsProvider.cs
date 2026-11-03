using System.Diagnostics;
using System.Runtime.InteropServices;

using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

/// <summary>
/// Reads machine metrics. Performance counters are created lazily and failures are tolerated:
/// on hosts where the counter registry is corrupt or the service account cannot read counters,
/// CPU falls back to <c>GetSystemTimes</c> deltas and RAM to <c>GlobalMemoryStatusEx</c>. If a
/// metric is unavailable from every source, <see cref="GetCpuUsage"/> returns
/// <see cref="float.NaN"/> and <see cref="GetRamMetric"/> returns a NaN usage so the calling
/// worker skips that cycle instead of the whole agent failing to start.
/// </summary>
public class SystemMetricsProvider : ISystemMetricsProvider
{
    private const int ProcessCpuSampleMilliseconds = 500;

    /// <summary>How long to wait before trying to re-create a counter that failed.</summary>
    internal static readonly TimeSpan CounterRetryInterval = TimeSpan.FromMinutes(10);

    private readonly LazyCounter _cpuCounter;
    private readonly LazyCounter _ramCounter;
    private readonly Func<SystemTimesSample?> _readSystemTimes;
    private readonly Func<MemoryStatusSample?> _readMemoryStatus;
    private readonly ILogger<SystemMetricsProvider> _logger;

    private readonly object _cpuFallbackLock = new();
    private SystemTimesSample? _lastSystemTimes;
    private bool _cpuFallbackFailureLogged;
    private bool _ramFallbackFailureLogged;

    public SystemMetricsProvider(ILogger<SystemMetricsProvider> logger)
        : this(
            logger,
            static (category, counter, instance) => new PerformanceCounterAdapter(category, counter, instance),
            ReadSystemTimes,
            ReadMemoryStatus,
            TimeProvider.System)
    {
    }

    internal SystemMetricsProvider(
        ILogger<SystemMetricsProvider> logger,
        Func<string, string, string?, IMetricCounter> counterFactory,
        Func<SystemTimesSample?> readSystemTimes,
        Func<MemoryStatusSample?> readMemoryStatus,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _readSystemTimes = readSystemTimes;
        _readMemoryStatus = readMemoryStatus;

        // Nothing is created here: a broken counter registry must not stop the host from
        // constructing this singleton (which previously took every worker down with it).
        _cpuCounter = new LazyCounter(
            "Processor", "% Processor Time", "_Total", primeOnCreate: true, counterFactory, timeProvider, logger);
        _ramCounter = new LazyCounter(
            "Memory", "Available MBytes", null, primeOnCreate: false, counterFactory, timeProvider, logger);

        // Baseline for the GetSystemTimes fallback so its first reading covers a real interval.
        _lastSystemTimes = SafeReadSystemTimes();
    }

    public float GetCpuUsage()
    {
        // Always advance the fallback baseline so switching to it yields a fresh interval.
        var fallback = SampleCpuFromSystemTimes();

        return _cpuCounter.TryRead(out var value) ? value : fallback;
    }

    public RamMetric GetRamMetric()
    {
        var now = DateTime.UtcNow;
        var memory = SafeReadMemoryStatus();
        var totalMb = memory?.TotalMb ?? GetGcTotalMemoryMb();

        double availableMb;
        if (_ramCounter.TryRead(out var counterValue))
        {
            availableMb = counterValue;
        }
        else if (memory is { } status)
        {
            availableMb = status.AvailableMb;
        }
        else
        {
            if (!_ramFallbackFailureLogged)
            {
                _logger.LogWarning(
                    "RAM metrics are unavailable from both the performance counter and GlobalMemoryStatusEx");
                _ramFallbackFailureLogged = true;
            }
            return new RamMetric(now, double.NaN, double.NaN, totalMb);
        }

        _ramFallbackFailureLogged = false;
        var usagePercent = totalMb > 0 ? (totalMb - availableMb) / totalMb * 100.0 : double.NaN;
        return new RamMetric(now, usagePercent, availableMb, totalMb);
    }

    // Inventories are read without the error tolerance of the metric reads: a failure must
    // surface (and skip retirement) rather than look like every resource disappeared.

    public ResourceInventory GetDiskInventory() => new(DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed)
        .Select(d => d.Name)
        .ToList());

    public ResourceInventory GetDiskQueueInventory() =>
        new(new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames().Where(i => i != "_Total").ToList());

    public ResourceInventory GetNetworkInventory() =>
        new(new PerformanceCounterCategory("Network Interface").GetInstanceNames().ToList());

    public IReadOnlyList<DiskMetric> GetDiskMetrics()
    {
        var metrics = new List<DiskMetric>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate drives");
            return metrics;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                var totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                var freeGb = drive.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
                var usagePercent = totalGb > 0 ? ((totalGb - freeGb) / totalGb) * 100.0 : 0;

                metrics.Add(new DiskMetric(DateTime.UtcNow, drive.Name, totalGb, freeGb, usagePercent));
            }
            catch (Exception ex)
            {
                // A drive can go not-ready between IsReady and TotalSize (e.g. a failing disk).
                _logger.LogDebug(ex, "Failed to read drive {Drive}", drive.Name);
            }
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

    private float SampleCpuFromSystemTimes()
    {
        lock (_cpuFallbackLock)
        {
            var current = SafeReadSystemTimes();
            var previous = _lastSystemTimes;
            _lastSystemTimes = current;

            if (current is not { } now)
            {
                if (!_cpuFallbackFailureLogged)
                {
                    _logger.LogWarning("GetSystemTimes failed; the CPU fallback is unavailable");
                    _cpuFallbackFailureLogged = true;
                }
                return float.NaN;
            }

            _cpuFallbackFailureLogged = false;
            if (previous is not { } prev)
                return float.NaN;

            // Kernel time includes idle time, so busy = (kernel + user) - idle.
            var idle = now.IdleTicks - prev.IdleTicks;
            var total = (now.KernelTicks - prev.KernelTicks) + (now.UserTicks - prev.UserTicks);
            if (total == 0 || idle > total)
                return float.NaN;

            return (float)Math.Clamp((total - idle) * 100.0 / total, 0, 100);
        }
    }

    private SystemTimesSample? SafeReadSystemTimes()
    {
        try
        {
            return _readSystemTimes();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetSystemTimes threw");
            return null;
        }
    }

    private MemoryStatusSample? SafeReadMemoryStatus()
    {
        try
        {
            return _readMemoryStatus();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GlobalMemoryStatusEx threw");
            return null;
        }
    }

    private double GetGcTotalMemoryMb()
    {
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

    // ── Native fallbacks ─────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    /// <summary>
    /// Machine-wide physical memory. Uses installed RAM (not the GC/process limit) so the
    /// percentage is correct on memory-limited/containerised hosts.
    /// </summary>
    private static MemoryStatusSample? ReadMemoryStatus()
    {
        var status = new MEMORYSTATUSEX();
        if (!GlobalMemoryStatusEx(status) || status.ullTotalPhys == 0)
            return null;
        return new MemoryStatusSample(
            status.ullAvailPhys / (1024.0 * 1024.0),
            status.ullTotalPhys / (1024.0 * 1024.0));
    }

    private static SystemTimesSample? ReadSystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user)
            ? new SystemTimesSample((ulong)idle, (ulong)kernel, (ulong)user)
            : null;

    public void Dispose()
    {
        _cpuCounter.Dispose();
        _ramCounter.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Lazy counter plumbing ────────────────────────────────────────────────

    /// <summary>Minimal counter surface so tests can simulate a broken counter registry.</summary>
    internal interface IMetricCounter : IDisposable
    {
        float NextValue();
    }

    internal readonly record struct SystemTimesSample(ulong IdleTicks, ulong KernelTicks, ulong UserTicks);

    internal readonly record struct MemoryStatusSample(double AvailableMb, double TotalMb);

    private sealed class PerformanceCounterAdapter : IMetricCounter
    {
        private readonly PerformanceCounter _counter;

        public PerformanceCounterAdapter(string category, string counter, string? instance) =>
            _counter = instance == null
                ? new PerformanceCounter(category, counter, readOnly: true)
                : new PerformanceCounter(category, counter, instance, readOnly: true);

        public float NextValue() => _counter.NextValue();

        public void Dispose() => _counter.Dispose();
    }

    /// <summary>
    /// Creates its counter on first use, retries creation at most every
    /// <see cref="CounterRetryInterval"/> after a failure, and logs each outage once.
    /// </summary>
    private sealed class LazyCounter : IDisposable
    {
        private readonly string _category;
        private readonly string _counterName;
        private readonly string? _instance;
        private readonly bool _primeOnCreate;
        private readonly Func<string, string, string?, IMetricCounter> _factory;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly object _lock = new();

        private IMetricCounter? _counter;
        private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
        private bool _outageLogged;
        private bool _disposed;

        public LazyCounter(
            string category,
            string counterName,
            string? instance,
            bool primeOnCreate,
            Func<string, string, string?, IMetricCounter> factory,
            TimeProvider time,
            ILogger logger)
        {
            _category = category;
            _counterName = counterName;
            _instance = instance;
            _primeOnCreate = primeOnCreate;
            _factory = factory;
            _time = time;
            _logger = logger;
        }

        private string Name => _instance == null
            ? $"\\{_category}\\{_counterName}"
            : $"\\{_category}({_instance})\\{_counterName}";

        public bool TryRead(out float value)
        {
            value = float.NaN;
            lock (_lock)
            {
                if (_disposed)
                    return false;

                if (_counter == null)
                {
                    if (_time.GetUtcNow() < _nextAttempt)
                        return false;

                    try
                    {
                        _counter = _factory(_category, _counterName, _instance);
                        if (_primeOnCreate)
                        {
                            // Rate counters return 0 on their first sample; discard it and let
                            // the caller use its fallback for this one reading.
                            _counter.NextValue();
                            MarkRecovered();
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Fail(ex, "could not be created");
                        return false;
                    }
                }

                try
                {
                    var read = _counter.NextValue();
                    if (!float.IsFinite(read))
                        return false;
                    value = read;
                    MarkRecovered();
                    return true;
                }
                catch (Exception ex)
                {
                    Fail(ex, "could not be read");
                    return false;
                }
            }
        }

        private void MarkRecovered()
        {
            if (_outageLogged)
                _logger.LogInformation("Performance counter {Counter} is available again", Name);
            _outageLogged = false;
        }

        private void Fail(Exception ex, string what)
        {
            try
            {
                _counter?.Dispose();
            }
            catch
            {
                // best effort
            }
            _counter = null;
            _nextAttempt = _time.GetUtcNow() + CounterRetryInterval;

            if (_outageLogged)
            {
                _logger.LogDebug(ex, "Performance counter {Counter} still unavailable", Name);
                return;
            }

            _outageLogged = true;
            _logger.LogWarning(ex,
                "Performance counter {Counter} {What}; using the native fallback and retrying every {Minutes} min",
                Name, what, CounterRetryInterval.TotalMinutes);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _counter?.Dispose();
                _counter = null;
            }
        }
    }
}