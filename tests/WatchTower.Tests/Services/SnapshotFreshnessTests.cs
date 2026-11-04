using AwesomeAssertions;

using WatchTower.Configuration;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Endpoints;
using WatchTower.Tests.Alerting;

namespace WatchTower.Tests.Services;

/// <summary>The freshness windows of the snapshot entries (the one place they are computed).</summary>
public class SnapshotFreshnessTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(1, 600)]          // 3 s, raised to the 10 min minimum
    [InlineData(30, 600)]         // default service / pool / network interval: 90 s -> 10 min
    [InlineData(60, 600)]         // default disk interval: 180 s -> 10 min
    [InlineData(200, 600)]        // exactly 10 min
    [InlineData(201, 603)]        // just above the minimum: 3 x interval
    [InlineData(300, 900)]
    [InlineData(3600, 10_800)]
    [InlineData(0, 600)]          // the monitors poll at least every second
    [InlineData(-5, 600)]
    public void PolledWindow_IsThreeIntervals_AtLeastTenMinutes(int pollIntervalSeconds, int expectedSeconds)
    {
        SnapshotFreshness.PolledWindow(pollIntervalSeconds).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(60, 10, 600)]             // default check: 130 s -> 10 min
    [InlineData(5, 1, 600)]               // shortest valid check
    [InlineData(295, 10, 600)]            // exactly 10 min
    [InlineData(300, 10, 610)]            // 2 x 300 + 10
    [InlineData(3600, 30, 7230)]          // hourly: 2 h 30 s
    [InlineData(86_400, 120, 172_920)]    // daily with the longest timeout: 48 h 2 min
    // Invalid configuration (a ConfigurationError check): clamped to the valid ranges.
    [InlineData(0, 0, 600)]
    [InlineData(1_000_000, 10, 172_810)]
    [InlineData(int.MaxValue, int.MaxValue, 172_920)]
    [InlineData(3600, 0, 7201)]
    public void EndpointWindow_IsTwoIntervalsPlusTheTimeout_AtLeastTenMinutes(int interval, int timeout, int expectedSeconds)
    {
        SnapshotFreshness.EndpointWindow(interval, timeout).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void TheEndpointClampRanges_AreTheOnesAValidCheckHas()
    {
        SnapshotFreshness.EndpointMinIntervalSeconds.Should().Be(EndpointCheck.MinIntervalSeconds);
        SnapshotFreshness.EndpointMaxIntervalSeconds.Should().Be(EndpointCheck.MaxIntervalSeconds).And.Be(86_400);
        SnapshotFreshness.EndpointMaxTimeoutSeconds.Should().Be(EndpointCheck.MaxTimeoutSeconds);
        SnapshotFreshness.MinimumWindow.Should().Be(TenMinutes);
    }

    [Fact]
    public void IsStale_OnlyBeyondTheWindow()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        SnapshotFreshness.IsStale(now - TenMinutes, now, TenMinutes).Should().BeFalse("exactly at the edge is still fresh");
        SnapshotFreshness.IsStale(now - TenMinutes - TimeSpan.FromTicks(1), now, TenMinutes).Should().BeTrue();
        SnapshotFreshness.IsStale(now - TimeSpan.FromDays(100), now, TenMinutes).Should().BeTrue();
        SnapshotFreshness.IsStale(now + TimeSpan.FromHours(1), now, TenMinutes).Should().BeFalse("a reading from the future is not stale");
        SnapshotFreshness.IsStale(DateTime.SpecifyKind(now - TimeSpan.FromMinutes(11), DateTimeKind.Unspecified), now, TenMinutes)
            .Should().BeTrue("stored timestamps without a kind are UTC");
    }

    [Fact]
    public void Policy_UsesThePollIntervalOfTheMonitorThatReadsEachKind()
    {
        var policy = Policy(
            disk: new DiskMonitorOptions { PollIntervalSeconds = 400 },
            network: new NetworkMonitorOptions { PollIntervalSeconds = 500 },
            services: new ServiceMonitorOptions { PollIntervalSeconds = 600 },
            pools: new ApplicationPoolMonitorOptions { PollIntervalSeconds = 700 });

        policy.Window(SnapshotKind.Disk, @"C:\").Should().Be(TimeSpan.FromSeconds(1200));
        policy.Window(SnapshotKind.DiskQueue, "0 C:").Should().Be(TimeSpan.FromSeconds(1200), "physical disks are read by the disk monitor");
        policy.Window(SnapshotKind.Network, "Ethernet").Should().Be(TimeSpan.FromSeconds(1500));
        policy.Window(SnapshotKind.Service, "W3SVC").Should().Be(TimeSpan.FromSeconds(1800));
        policy.Window(SnapshotKind.ApplicationPool, "DefaultAppPool").Should().Be(TimeSpan.FromSeconds(2100));
    }

    [Fact]
    public void Policy_FollowsConfigurationChanges()
    {
        var disk = new TestOptionsMonitor<DiskMonitorOptions>(new DiskMonitorOptions { PollIntervalSeconds = 60 });
        var policy = new SnapshotFreshnessPolicy(disk,
            new TestOptionsMonitor<NetworkMonitorOptions>(new()), new TestOptionsMonitor<ServiceMonitorOptions>(new()),
            new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new()), new TestOptionsMonitor<EndpointMonitorOptions>(new()));
        policy.Window(SnapshotKind.Disk, @"C:\").Should().Be(TenMinutes);

        disk.CurrentValue = new DiskMonitorOptions { PollIntervalSeconds = 1800 }; // e.g. pushed by remote config

        policy.Window(SnapshotKind.Disk, @"C:\").Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Policy_UsesEachEndpointChecksOwnIntervalAndTimeout()
    {
        var policy = Policy(endpoints:
        [
            new EndpointCheckOptions { Name = "daily-cert", Type = "Certificate", IntervalSeconds = 86_400, TimeoutSeconds = 20 },
            new EndpointCheckOptions { Name = "shop-home", IntervalSeconds = 60, TimeoutSeconds = 10 },
            new EndpointCheckOptions { Name = "Shop-Home", IntervalSeconds = 3600, TimeoutSeconds = 10 }, // duplicate: not run
            new EndpointCheckOptions { Name = "  broken ", IntervalSeconds = 0, TimeoutSeconds = 500 },  // invalid
        ]);

        policy.Window(SnapshotKind.Endpoint, "daily-cert").Should().Be(TimeSpan.FromSeconds(2 * 86_400 + 20));
        policy.Window(SnapshotKind.Endpoint, "SHOP-HOME").Should().Be(TenMinutes, "names match case-insensitively, first entry wins");
        policy.Window(SnapshotKind.Endpoint, "broken").Should().Be(TenMinutes, "2 x 5 s + 120 s, clamped, is below the minimum");
        policy.Window(SnapshotKind.Endpoint, "no-longer-configured").Should().Be(TenMinutes, "a default check's window");
    }

    [Fact]
    public void Policy_IsStale_AppliesTheWindowOfTheEntry()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var policy = Policy(endpoints: [new EndpointCheckOptions { Name = "hourly", IntervalSeconds = 3600, TimeoutSeconds = 30 }]);

        policy.IsStale(SnapshotKind.Endpoint, "hourly", now - TimeSpan.FromMinutes(90), now).Should().BeFalse("an hourly check is fresh for 2 h 30 s");
        policy.IsStale(SnapshotKind.Endpoint, "hourly", now - TimeSpan.FromMinutes(121), now).Should().BeTrue();
        policy.IsStale(SnapshotKind.Disk, @"D:\", now - TimeSpan.FromMinutes(11), now).Should().BeTrue();
        SnapshotFreshnessPolicy.Default.Window(SnapshotKind.Disk, @"D:\").Should().Be(TenMinutes);
    }

    private static SnapshotFreshnessPolicy Policy(
        DiskMonitorOptions? disk = null, NetworkMonitorOptions? network = null, ServiceMonitorOptions? services = null,
        ApplicationPoolMonitorOptions? pools = null, List<EndpointCheckOptions>? endpoints = null) => new(
        new TestOptionsMonitor<DiskMonitorOptions>(disk ?? new()),
        new TestOptionsMonitor<NetworkMonitorOptions>(network ?? new()),
        new TestOptionsMonitor<ServiceMonitorOptions>(services ?? new()),
        new TestOptionsMonitor<ApplicationPoolMonitorOptions>(pools ?? new()),
        new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions { Endpoints = endpoints ?? [] }));
}