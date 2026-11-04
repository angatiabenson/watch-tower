using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using WatchTower.Services;

using static WatchTower.Services.SystemMetricsProvider;

namespace WatchTower.Tests.Services;

public class SystemMetricsProviderTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private int _counterCreations;
    private bool _countersBroken = true;
    private float _counterValue = 42f;

    private readonly Queue<SystemTimesSample?> _systemTimes = new();
    private MemoryStatusSample? _memory = new(AvailableMb: 4096, TotalMb: 16384);

    private sealed class FakeCounter(Func<float> read) : IMetricCounter
    {
        public float NextValue() => read();
        public void Dispose() { }
    }

    private SystemMetricsProvider CreateProvider() => new(
        NullLogger<SystemMetricsProvider>.Instance,
        (_, _, _) =>
        {
            _counterCreations++;
            if (_countersBroken)
                throw new InvalidOperationException("Category does not exist.");
            return new FakeCounter(() => _counterValue);
        },
        () => _systemTimes.Count > 0 ? _systemTimes.Dequeue() : null,
        () => _memory,
        _time);

    [Fact]
    public void Constructor_DoesNotTouchPerformanceCounters()
    {
        using var provider = CreateProvider();

        // Counter creation used to happen eagerly here and crash the host on broken registries.
        _counterCreations.Should().Be(0);
    }

    [Fact]
    public void GetCpuUsage_CounterUnavailable_FallsBackToSystemTimes()
    {
        // Baseline read in the constructor, then: +100 idle out of +400 total => 75% busy.
        _systemTimes.Enqueue(new SystemTimesSample(IdleTicks: 1000, KernelTicks: 2000, UserTicks: 1000));
        _systemTimes.Enqueue(new SystemTimesSample(IdleTicks: 1100, KernelTicks: 2200, UserTicks: 1200));
        using var provider = CreateProvider();

        provider.GetCpuUsage().Should().BeApproximately(75f, 0.01f);
    }

    [Fact]
    public void GetCpuUsage_NoSourceAvailable_ReturnsNaN()
    {
        using var provider = CreateProvider();

        float.IsNaN(provider.GetCpuUsage()).Should().BeTrue();
    }

    [Fact]
    public void GetCpuUsage_CounterCreationIsRetriedOnlyAfterBackoff()
    {
        using var provider = CreateProvider();

        provider.GetCpuUsage();
        provider.GetCpuUsage();
        _counterCreations.Should().Be(1);

        _time.Advance(CounterRetryInterval);
        provider.GetCpuUsage();
        _counterCreations.Should().Be(2);
    }

    [Fact]
    public void GetCpuUsage_UsesCounterOnceItRecovers()
    {
        using var provider = CreateProvider();
        provider.GetCpuUsage(); // creation fails

        _countersBroken = false;
        _time.Advance(CounterRetryInterval);
        provider.GetCpuUsage(); // created + primed (first sample discarded)

        provider.GetCpuUsage().Should().Be(42f);
    }

    [Fact]
    public void GetCpuUsage_NonFiniteCounterValue_UsesFallback()
    {
        _countersBroken = false;
        using var provider = CreateProvider();
        provider.GetCpuUsage(); // prime
        provider.GetCpuUsage().Should().Be(42f);

        _counterValue = float.NaN;
        _systemTimes.Enqueue(new SystemTimesSample(0, 100, 0));
        _systemTimes.Enqueue(new SystemTimesSample(50, 200, 0));

        // A non-finite counter value is treated as unavailable rather than recorded.
        float.IsNaN(provider.GetCpuUsage()).Should().BeTrue();
        provider.GetCpuUsage().Should().BeApproximately(50f, 0.01f);
    }

    [Fact]
    public void GetRamMetric_CounterUnavailable_FallsBackToGlobalMemoryStatus()
    {
        using var provider = CreateProvider();

        var ram = provider.GetRamMetric();

        ram.AvailableMb.Should().Be(4096);
        ram.TotalMb.Should().Be(16384);
        ram.UsagePercent.Should().BeApproximately(75, 0.01);
    }

    [Fact]
    public void GetRamMetric_PrefersCounterWhenAvailable()
    {
        _countersBroken = false;
        _counterValue = 8192;
        using var provider = CreateProvider();

        var ram = provider.GetRamMetric();

        ram.AvailableMb.Should().Be(8192);
        ram.UsagePercent.Should().BeApproximately(50, 0.01);
    }

    [Fact]
    public void GetRamMetric_NoSourceAvailable_ReturnsNaNUsage()
    {
        _memory = null;
        using var provider = CreateProvider();

        double.IsNaN(provider.GetRamMetric().UsagePercent).Should().BeTrue();
    }

    [Fact]
    public void RealProvider_ConstructsAndReadsWithoutThrowing()
    {
        using var provider = new SystemMetricsProvider(NullLogger<SystemMetricsProvider>.Instance);

        var act = () =>
        {
            provider.GetCpuUsage();
            provider.GetRamMetric();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void RealProvider_DiskInventoryListsTheSystemDrive_AndEveryDriveWithMetrics()
    {
        using var provider = new SystemMetricsProvider(NullLogger<SystemMetricsProvider>.Instance);

        var inventory = provider.GetDiskInventory().Names;

        inventory.Should().Contain(n => string.Equals(n, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        inventory.Should().Contain(provider.GetDiskMetrics().Select(d => d.DriveName),
            "a drive with metrics must never look removed");
    }
}