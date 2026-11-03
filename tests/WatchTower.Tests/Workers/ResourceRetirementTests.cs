using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

/// <summary>
/// Alerts of discovered resources (drives, physical disks, network adapters) that disappear are
/// retired once the resource has been missing for <see cref="AbsentResourceTracker.MissedPolls"/>
/// successful listings and <see cref="AbsentResourceTracker.GracePeriod"/>, never on a failed or
/// suspicious (empty disk) listing.
/// </summary>
public class ResourceRetirementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<ISystemMetricsProvider> _provider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private IAlertStateStore _state = new InMemoryAlertStateStore();

    private static readonly AlertKey DriveD = AlertKey.Disk("D:\\");
    private static readonly AlertKey QueueD = AlertKey.DiskQueue("1 D:");
    private static readonly AlertKey Usb = AlertKey.Network("USB Ethernet");

    // Current listings; a null listing makes the inventory read throw.
    private string[]? _drives = ["C:\\", "D:\\"];
    private string[]? _queues = ["0 C:", "1 D:"];
    private string[]? _adapters = ["Ethernet", "USB Ethernet"];
    private readonly string[] _critical = ["C:\\", "D:\\"];
    private readonly string[] _saturated = ["USB Ethernet"];

    public ResourceRetirementTests()
    {
        DateTime Now() => _time.GetUtcNow().UtcDateTime;
        _provider.Setup(p => p.GetDiskMetrics()).Returns(() => (_drives ?? []).Select(d =>
            new DiskMetric(Now(), d, 500, _critical.Contains(d) ? 10 : 400, _critical.Contains(d) ? 98 : 20)).ToList());
        _provider.Setup(p => p.GetDiskQueueMetrics()).Returns(() => (_queues ?? []).Select(q =>
            new DiskQueueMetric(Now(), q, q == "1 D:" ? 9 : 0)).ToList());
        _provider.Setup(p => p.GetNetworkMetrics()).Returns(() => (_adapters ?? []).Select(a =>
            new NetworkMetric(Now(), a, _saturated.Contains(a) ? 50 : 0)).ToList());
        _provider.Setup(p => p.GetDiskInventory()).Returns(() => Listing(_drives));
        _provider.Setup(p => p.GetDiskQueueInventory()).Returns(() => Listing(_queues));
        _provider.Setup(p => p.GetNetworkInventory()).Returns(() => Listing(_adapters));
    }

    private static ResourceInventory Listing(string[]? names) =>
        names is null ? throw new InvalidOperationException("counters unavailable") : new ResourceInventory(names);

    private AlertManager Alerts() => TestAlerts.Create(_metricsStore.Object, _time, _dispatcher, state: _state);

    private DiskMonitorWorker DiskWorker(IAlertManager alerts) => new(
        _provider.Object, _metricsStore.Object, alerts,
        new TestOptionsMonitor<DiskMonitorOptions>(new DiskMonitorOptions
        {
            PollIntervalSeconds = 60,
            WarningThresholdPercent = 70,
            CriticalThresholdPercent = 85,
            QueueLengthWarningThreshold = 2,
        }),
        _time, NullLogger<DiskMonitorWorker>.Instance);

    private NetworkMonitorWorker NetworkWorker(IAlertManager alerts) => new(
        _provider.Object, _metricsStore.Object, alerts,
        new TestOptionsMonitor<NetworkMonitorOptions>(new NetworkMonitorOptions { PollIntervalSeconds = 60, OutputQueueWarningThreshold = 10 }),
        _time, NullLogger<NetworkMonitorWorker>.Instance);

    /// <summary>Raises the D: disk, D: disk queue and USB adapter alerts (C: is critical too).</summary>
    private async Task<(AlertManager Alerts, DiskMonitorWorker Disks, NetworkMonitorWorker Network)> RaiseAll()
    {
        var alerts = Alerts();
        var disks = DiskWorker(alerts);
        var network = NetworkWorker(alerts);
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);
        alerts.GetOpenAlerts().Select(a => a.Key).Should().BeEquivalentTo(
            [AlertKey.Disk("C:\\").Value, DriveD.Value, QueueD.Value, Usb.Value]);
        return (alerts, disks, network);
    }

    /// <summary>Polls both workers every minute for <paramref name="duration"/> (first poll right away).</summary>
    private async Task PollFor(TimeSpan duration, params object[] workers)
    {
        for (var elapsed = TimeSpan.Zero; elapsed <= duration; elapsed += TimeSpan.FromMinutes(1))
        {
            if (elapsed > TimeSpan.Zero)
                _time.Advance(TimeSpan.FromMinutes(1));
            foreach (var worker in workers)
            {
                if (worker is DiskMonitorWorker d)
                    await d.PollOnceAsync(Ct);
                else
                    await ((NetworkMonitorWorker)worker).PollOnceAsync(Ct);
            }
        }
    }

    private AlertState Stored(AlertKey key) => _state.LoadAll().Single(s => s.Key == key.Value);

    [Fact]
    public async Task RemovedDriveDiskAndAdapter_AreRetiredAfterTheGracePeriod_WithAnExplicitReason()
    {
        var (alerts, disks, network) = await RaiseAll();
        _drives = ["C:\\"];
        _queues = ["0 C:"];
        _adapters = ["Ethernet"];

        await PollFor(TimeSpan.FromMinutes(9), disks, network);
        alerts.GetOpenAlerts().Should().HaveCount(4, "missing for 9 minutes (10 listings) is still within the grace period");

        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);

        alerts.GetOpenAlerts().Should().ContainSingle().Which.Key.Should().Be(AlertKey.Disk("C:\\").Value);
        Stored(DriveD).RetiredReason.Should().Be("Disk D: is no longer present");
        Stored(QueueD).RetiredReason.Should().Be("Physical disk 1 D: is no longer present");
        Stored(Usb).RetiredReason.Should().Be("Network adapter USB Ethernet is no longer present");
        _dispatcher.Sent.Where(n => n.Kind == AlertNotificationKind.Resolved).Select(n => (n.AlertKey, n.RetiredReason))
            .Should().BeEquivalentTo([
                (DriveD.Value, "Disk D: is no longer present"),
                (QueueD.Value, "Physical disk 1 D: is no longer present"),
                (Usb.Value, "Network adapter USB Ethernet is no longer present"),
            ]);
    }

    [Fact]
    public async Task TenMinutesAreNotEnough_WithoutThreeMissedListings()
    {
        var (alerts, disks, network) = await RaiseAll();
        _drives = ["C:\\"];
        _adapters = ["Ethernet"];

        // Listings 15 minutes apart: the third is the first to retire.
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(15));
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);
        alerts.IsOpen(DriveD).Should().BeTrue("missing from only two listings");
        alerts.IsOpen(Usb).Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(15));
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);
        alerts.IsOpen(DriveD).Should().BeFalse();
        alerts.IsOpen(Usb).Should().BeFalse();
    }

    [Fact]
    public async Task FailedListings_NeverRetire_AndDoNotCountAsMisses()
    {
        var (alerts, disks, network) = await RaiseAll();
        _drives = null;
        _queues = null;
        _adapters = null;

        // The judge's probe, with failing reads: two days of polls.
        await PollFor(TimeSpan.FromDays(2), disks, network);
        alerts.GetOpenAlerts().Should().HaveCount(4);

        // Readable again but D: and the adapter are gone: the grace period starts only now.
        _drives = ["C:\\"];
        _queues = ["0 C:"];
        _adapters = ["Ethernet"];
        await PollFor(TimeSpan.FromMinutes(9), disks, network);
        alerts.GetOpenAlerts().Should().HaveCount(4);
        await PollFor(TimeSpan.FromMinutes(1), disks, network);
        alerts.GetOpenAlerts().Should().ContainSingle();
    }

    [Fact]
    public async Task FailedMetricsRead_SkipsRetirement()
    {
        var (alerts, disks, network) = await RaiseAll();
        _drives = ["C:\\"];
        _adapters = ["Ethernet"];
        _provider.Setup(p => p.GetDiskMetrics()).Throws(new InvalidOperationException("WMI broken"));
        _provider.Setup(p => p.GetNetworkMetrics()).Throws(new InvalidOperationException("counters broken"));
        _provider.Invocations.Clear();

        await PollFor(TimeSpan.FromDays(2), disks, network);

        alerts.GetOpenAlerts().Should().HaveCount(4);
        _provider.Verify(p => p.GetDiskInventory(), Times.Never);
        _provider.Verify(p => p.GetNetworkInventory(), Times.Never);
    }

    [Fact]
    public async Task EmptyDiskListings_AreSuspicious_ButNoAdaptersAtAllCounts()
    {
        // The judge's probe: every listing comes back empty for two days.
        var (alerts, disks, network) = await RaiseAll();
        _drives = [];
        _queues = [];
        _adapters = [];

        await PollFor(TimeSpan.FromDays(2), disks, network);

        alerts.GetOpenAlerts().Select(a => a.Key).Should().BeEquivalentTo(
            [AlertKey.Disk("C:\\").Value, DriveD.Value, QueueD.Value],
            "a host always has a system drive, so an empty disk listing is a broken read");
        Stored(Usb).RetiredReason.Should().Be("Network adapter USB Ethernet is no longer present");
    }

    [Fact]
    public async Task ResourceThatComesBackWithinTheGracePeriod_KeepsItsAlert_AndTheGracePeriodRestarts()
    {
        var (alerts, disks, network) = await RaiseAll();
        _drives = ["C:\\"];
        _adapters = ["Ethernet"];
        await PollFor(TimeSpan.FromMinutes(8), disks, network);

        // The USB disk is re-plugged and the adapter blips back for one poll.
        _drives = ["C:\\", "D:\\"];
        _adapters = ["Ethernet", "USB Ethernet"];
        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);

        _drives = ["C:\\"];
        _adapters = ["Ethernet"];
        await PollFor(TimeSpan.FromMinutes(9), disks, network);
        alerts.IsOpen(DriveD).Should().BeTrue("missing for 9 minutes since it was last seen");
        alerts.IsOpen(Usb).Should().BeTrue();

        await PollFor(TimeSpan.FromMinutes(1), disks, network);
        alerts.IsOpen(DriveD).Should().BeFalse();
        alerts.IsOpen(Usb).Should().BeFalse();
    }

    [Fact]
    public async Task DriveThatIsListedButCannotBeRead_IsNotRetired()
    {
        // A failing drive drops out of the usage metrics (not ready) but is still listed.
        var (alerts, disks, _) = await RaiseAll();
        _provider.Setup(p => p.GetDiskMetrics()).Returns(() =>
            [new DiskMetric(_time.GetUtcNow().UtcDateTime, "C:\\", 500, 10, 98)]);

        await PollFor(TimeSpan.FromDays(2), disks);

        alerts.IsOpen(DriveD).Should().BeTrue();
    }

    [Fact]
    public async Task OrphanAlertsPersistedBeforeARestart_AreRetiredOnceTheGracePeriodHasPassedSinceStartup()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        _state = new SqliteAlertStateStore(dir.Paths.DatabasePath);
        await RaiseAll();

        // Restart days later: D: and the adapter are gone by now.
        _time.Advance(TimeSpan.FromDays(3));
        _drives = ["C:\\"];
        _queues = ["0 C:"];
        _adapters = ["Ethernet"];
        _state = new SqliteAlertStateStore(dir.Paths.DatabasePath);
        var alerts = Alerts();
        var disks = DiskWorker(alerts);
        var network = NetworkWorker(alerts);
        alerts.GetOpenAlerts().Should().HaveCount(4, "the alerts were persisted");

        await PollFor(TimeSpan.FromMinutes(9), disks, network);
        alerts.GetOpenAlerts().Should().HaveCount(4, "the grace period counts from startup, not from the alert's age");

        await PollFor(TimeSpan.FromMinutes(1), disks, network);
        alerts.GetOpenAlerts().Should().ContainSingle().Which.Key.Should().Be(AlertKey.Disk("C:\\").Value);
        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadAll()
            .Where(s => s.RetiredReason is not null).Select(s => s.Key)
            .Should().BeEquivalentTo([DriveD.Value, QueueD.Value, Usb.Value]);
    }
}