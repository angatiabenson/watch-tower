using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Services.Endpoints;
using WatchTower.Tests.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

/// <summary>
/// The latest-state rows that every metrics batch repeats are pruned when their resource is gone:
/// discovered resources (drives, physical disks, network adapters) through the
/// <see cref="AbsentResourceTracker"/> grace logic, configured ones (services, application pools,
/// endpoint checks) as soon as the configuration no longer contains them.
/// </summary>
public sealed class SnapshotPruningTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_snap_{Guid.NewGuid()}.db");
    private readonly SqliteMetricsStore _store;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-04T00:00:00Z"));
    private readonly Mock<ISystemMetricsProvider> _provider = new();
    private readonly AlertManager _alerts;

    // Current listings; null makes the corresponding read throw.
    private string[]? _drives = ["C:\\", "D:\\"];
    private string[]? _driveMetrics = ["C:\\", "D:\\"];
    private string[]? _queues = ["0 C:", "1 D:"];
    private string[]? _adapters = ["Ethernet", "USB Ethernet"];
    private string[] _critical = [];

    public SnapshotPruningTests()
    {
        _store = new SqliteMetricsStore(_dbPath);
        _alerts = TestAlerts.Create(_store, _time, new RecordingDispatcher());

        DateTime Now() => _time.GetUtcNow().UtcDateTime;
        _provider.Setup(p => p.GetDiskMetrics()).Returns(() => (_driveMetrics ?? throw new IOException("drives unavailable"))
            .Select(d => new DiskMetric(Now(), d, 500, _critical.Contains(d) ? 10 : 400, _critical.Contains(d) ? 98 : 20)).ToList());
        _provider.Setup(p => p.GetDiskQueueMetrics()).Returns(() => (_queues ?? []).Select(q => new DiskQueueMetric(Now(), q, 0)).ToList());
        _provider.Setup(p => p.GetNetworkMetrics()).Returns(() => (_adapters ?? []).Select(a => new NetworkMetric(Now(), a, 0)).ToList());
        _provider.Setup(p => p.GetDiskInventory()).Returns(() => Listing(_drives));
        _provider.Setup(p => p.GetDiskQueueInventory()).Returns(() => Listing(_queues));
        _provider.Setup(p => p.GetNetworkInventory()).Returns(() => Listing(_adapters));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + ext); } catch (IOException) { /* best effort */ }
        }
    }

    private static ResourceInventory Listing(string[]? names) =>
        names is null ? throw new InvalidOperationException("counters unavailable") : new ResourceInventory(names);

    private DiskMonitorWorker DiskWorker() => new(
        _provider.Object, _store, _alerts,
        new TestOptionsMonitor<DiskMonitorOptions>(new DiskMonitorOptions
        {
            PollIntervalSeconds = 60,
            WarningThresholdPercent = 70,
            CriticalThresholdPercent = 85,
            QueueLengthWarningThreshold = 2,
        }),
        _time, NullLogger<DiskMonitorWorker>.Instance);

    private NetworkMonitorWorker NetworkWorker() => new(
        _provider.Object, _store, _alerts,
        new TestOptionsMonitor<NetworkMonitorOptions>(new NetworkMonitorOptions { PollIntervalSeconds = 60, OutputQueueWarningThreshold = 10 }),
        _time, NullLogger<NetworkMonitorWorker>.Instance);

    private IEnumerable<string> Drives => _store.GetLatestDiskMetrics().Select(d => d.DriveName);

    /// <summary>Polls once a minute from now until <paramref name="minutes"/> later (inclusive).</summary>
    private async Task PollEveryMinute(int minutes, Func<CancellationToken, Task> poll)
    {
        for (var minute = 0; minute <= minutes; minute++)
        {
            if (minute > 0)
                _time.Advance(TimeSpan.FromMinutes(1));
            await poll(Ct);
        }
    }

    /// <summary>Builds the next metrics batch the way the outbox worker does and returns its drives.</summary>
    private async Task<IReadOnlyList<string>> NextBatchDrives()
    {
        string? payload = null;
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(o => o.AddMessageAsync(OutboxDispatchWorker.MetricsMessageType, It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<long?>()))
            .Callback<string, string, int, long?, long?>((_, json, _, _, _) => payload = json)
            .Returns(Task.CompletedTask);
        outbox.Setup(o => o.GetPendingMessagesAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<OutboxMessage>());
        var sync = new Mock<IPortalSyncState>();
        sync.Setup(s => s.GetLastForwardedRowIdAsync(It.IsAny<string>())).ReturnsAsync(0L);
        var worker = new OutboxDispatchWorker(Mock.Of<IPortalClient>(), outbox.Object, sync.Object, _store, new MetricSnapshotTrigger(),
            new TestOptionsMonitor<PortalOptions>(new PortalOptions { Enabled = true }), _time, NullLogger<OutboxDispatchWorker>.Instance);

        await worker.RunCycleAsync(Ct);

        payload.Should().NotBeNull();
        var batch = JsonSerializer.Deserialize<MetricsBatchDto>(payload!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return batch.DiskMetrics.Select(d => d.DriveName).ToList();
    }

    [Fact]
    public async Task RemovedDisk_LeavesTheSnapshotAndTheNextBatch_OnlyAfterTheGracePeriod()
    {
        var disks = DiskWorker();
        await disks.PollOnceAsync(Ct);
        (await NextBatchDrives()).Should().BeEquivalentTo(["C:\\", "D:\\"]);

        _drives = _driveMetrics = ["C:\\"];

        // Missing from 10 listings over 9 minutes: 3 polls, but not yet 10 minutes.
        await PollEveryMinute(9, disks.PollOnceAsync);
        Drives.Should().BeEquivalentTo(["C:\\", "D:\\"]);
        _store.GetLatestDiskMetrics().Single(d => d.DriveName == "D:\\").Timestamp
            .Should().Be(DateTime.Parse("2026-11-04T00:00:00Z").ToUniversalTime(), "D: keeps its last (old) reading meanwhile");

        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);

        Drives.Should().BeEquivalentTo(["C:\\"]);
        (await NextBatchDrives()).Should().BeEquivalentTo(["C:\\"]);
    }

    [Fact]
    public async Task RemovedDisk_NeedsThreeSuccessfulListings_EvenAfterTenMinutes()
    {
        var disks = DiskWorker();
        await disks.PollOnceAsync(Ct);
        _drives = _driveMetrics = ["C:\\"];

        await disks.PollOnceAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        await disks.PollOnceAsync(Ct);
        Drives.Should().Contain("D:\\", "only two listings missed it so far");

        await disks.PollOnceAsync(Ct);
        Drives.Should().BeEquivalentTo(["C:\\"]);
    }

    [Fact]
    public async Task FailedReads_NeverCountTowardsRemoval()
    {
        var disks = DiskWorker();
        await disks.PollOnceAsync(Ct);
        _driveMetrics = ["C:\\"];

        // 30 minutes of failed inventory reads, then of empty (suspicious) ones, then of failed metric reads.
        _drives = null;
        await PollEveryMinute(30, disks.PollOnceAsync);
        _drives = [];
        await PollEveryMinute(30, disks.PollOnceAsync);
        _drives = ["C:\\"];
        _driveMetrics = null;
        await PollEveryMinute(30, disks.PollOnceAsync);
        Drives.Should().Contain("D:\\");

        // The grace period starts with the first successful listing that misses it.
        _driveMetrics = ["C:\\"];
        await PollEveryMinute(9, disks.PollOnceAsync);
        Drives.Should().Contain("D:\\");
        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        Drives.Should().BeEquivalentTo(["C:\\"]);
    }

    [Fact]
    public async Task DiskThatComesBack_IsReportedAgain_AndAShortAbsenceResetsTheGracePeriod()
    {
        var disks = DiskWorker();
        await disks.PollOnceAsync(Ct);

        // Gone for 8 minutes, back for one poll: the count starts over.
        _drives = _driveMetrics = ["C:\\"];
        await PollEveryMinute(8, disks.PollOnceAsync);
        _drives = _driveMetrics = ["C:\\", "D:\\"];
        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        _drives = _driveMetrics = ["C:\\"];
        _time.Advance(TimeSpan.FromMinutes(1));
        await PollEveryMinute(9, disks.PollOnceAsync);
        Drives.Should().Contain("D:\\");

        // Retired after a full grace period, then plugged in again: a new row with a fresh reading.
        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        Drives.Should().BeEquivalentTo(["C:\\"]);

        _drives = _driveMetrics = ["C:\\", "D:\\"];
        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        _store.GetLatestDiskMetrics().Single(d => d.DriveName == "D:\\").Timestamp.Should().Be(_time.GetUtcNow().UtcDateTime);
        (await NextBatchDrives()).Should().BeEquivalentTo(["C:\\", "D:\\"]);
    }

    [Fact]
    public async Task RemovedCriticalDisk_LosesItsAlertAndItsSnapshotTogether()
    {
        _critical = ["D:\\"];
        var disks = DiskWorker();
        await disks.PollOnceAsync(Ct);
        _alerts.GetOpenAlerts().Select(a => a.Key).Should().Contain(AlertKey.Disk("D:\\").Value);

        _drives = _driveMetrics = ["C:\\"];
        await PollEveryMinute(10, disks.PollOnceAsync);

        _alerts.GetOpenAlerts().Select(a => a.Key).Should().NotContain(AlertKey.Disk("D:\\").Value);
        Drives.Should().BeEquivalentTo(["C:\\"]);
    }

    [Fact]
    public async Task RemovedPhysicalDiskAndAdapter_AreRemovedAfterTheGracePeriod()
    {
        var disks = DiskWorker();
        var network = NetworkWorker();
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);

        _queues = ["0 C:"];
        _adapters = ["Ethernet"];
        await PollEveryMinute(9, async _ =>
        {
            await disks.PollOnceAsync(Ct);
            await network.PollOnceAsync(Ct);
        });
        _store.GetSnapshotNames(SnapshotKind.DiskQueue).Should().BeEquivalentTo(["0 C:", "1 D:"]);
        _store.GetSnapshotNames(SnapshotKind.Network).Should().BeEquivalentTo(["Ethernet", "USB Ethernet"]);

        _time.Advance(TimeSpan.FromMinutes(1));
        await disks.PollOnceAsync(Ct);
        await network.PollOnceAsync(Ct);

        _store.GetLatestDiskQueueMetrics().Select(q => q.InstanceName).Should().BeEquivalentTo(["0 C:"]);
        _store.GetLatestNetworkMetrics().Select(n => n.InterfaceName).Should().BeEquivalentTo(["Ethernet"]);
    }

    [Fact]
    public async Task NoAdaptersAtAll_IsGenuine_AndRemovesThemAfterTheGracePeriod()
    {
        var network = NetworkWorker();
        await network.PollOnceAsync(Ct);

        _adapters = [];
        await PollEveryMinute(10, network.PollOnceAsync);

        _store.GetLatestNetworkMetrics().Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceRemovedFromConfiguration_DisappearsOnTheNextPoll_AndComesBackWhenReAdded()
    {
        var manager = new Mock<IWindowsServiceManager>();
        manager.Setup(m => m.GetServiceStatus(It.IsAny<string>()))
            .Returns((string name) => new ServiceStatus(_time.GetUtcNow().UtcDateTime, name, name, "Running", true, "Automatic"));
        var options = new TestOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions
        {
            PollIntervalSeconds = 60,
            StartupGraceSeconds = 0,
            WatchedServices = ["Spooler", "W3SVC"],
        });
        var worker = new ServiceMonitorWorker(manager.Object, _store, _alerts, new NullPortalEventPublisher(), options, _time,
            NullLogger<ServiceMonitorWorker>.Instance);
        IEnumerable<string> Reported() => _store.GetLatestServiceStatuses().Select(s => s.ServiceName);

        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["Spooler", "W3SVC"]);

        options.CurrentValue = new ServiceMonitorOptions { PollIntervalSeconds = 60, WatchedServices = ["spooler"] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["spooler"], "the row of the old spelling must not linger next to the new one");

        options.CurrentValue = new ServiceMonitorOptions { PollIntervalSeconds = 60, WatchedServices = [] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEmpty();

        options.CurrentValue = new ServiceMonitorOptions { PollIntervalSeconds = 60, WatchedServices = ["W3SVC"] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["W3SVC"]);
    }

    [Fact]
    public async Task ApplicationPoolRemovedFromConfiguration_DisappearsOnTheNextPoll()
    {
        var manager = new Mock<IIisApplicationPoolManager>();
        manager.Setup(m => m.GetApplicationPoolStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => new ApplicationPoolStatus(_time.GetUtcNow().UtcDateTime, name, "Started", true));
        var options = new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions
        {
            PollIntervalSeconds = 60,
            WatchedApplicationPools = ["DefaultAppPool", "Api"],
        });
        var worker = new ApplicationPoolMonitorWorker(manager.Object, _store, _alerts, new NullPortalEventPublisher(), options, _time,
            NullLogger<ApplicationPoolMonitorWorker>.Instance);
        IEnumerable<string> Reported() => _store.GetLatestApplicationPoolStatuses().Select(s => s.ApplicationPoolName);

        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["DefaultAppPool", "Api"]);

        options.CurrentValue = new ApplicationPoolMonitorOptions { PollIntervalSeconds = 60, WatchedApplicationPools = ["DefaultAppPool"] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["DefaultAppPool"]);

        options.CurrentValue = new ApplicationPoolMonitorOptions { PollIntervalSeconds = 60, WatchedApplicationPools = ["DefaultAppPool", "Api"] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["DefaultAppPool", "Api"]);
    }

    [Fact]
    public async Task ApplicationPoolRenamedOnlyInCase_KeepsItsRow()
    {
        var manager = new Mock<IIisApplicationPoolManager>();
        manager.Setup(m => m.GetApplicationPoolStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => new ApplicationPoolStatus(_time.GetUtcNow().UtcDateTime, name, "Started", true));
        var options = new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions
        {
            PollIntervalSeconds = 60,
            WatchedApplicationPools = ["DefaultAppPool", "defaultapppool", " "],
        });
        var worker = new ApplicationPoolMonitorWorker(manager.Object, _store, _alerts, new NullPortalEventPublisher(), options, _time,
            NullLogger<ApplicationPoolMonitorWorker>.Instance);
        IEnumerable<string> Reported() => _store.GetLatestApplicationPoolStatuses().Select(s => s.ApplicationPoolName);

        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["DefaultAppPool"]);
        manager.Verify(m => m.GetApplicationPoolStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "names that only differ in case are one pool, and blank entries are skipped");

        // Renamed in case only while the pool cannot be read: its last row is kept, renamed.
        manager.Setup(m => m.GetApplicationPoolStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IIS unavailable"));
        options.CurrentValue = new ApplicationPoolMonitorOptions { PollIntervalSeconds = 60, WatchedApplicationPools = ["defaultapppool"] };
        var poll = () => worker.PollOnceAsync(Ct);
        await poll.Should().ThrowAsync<InvalidOperationException>();
        Reported().Should().BeEquivalentTo(["defaultapppool"]);
    }

    private static Mock<IEndpointProbe> UpProbe()
    {
        var probe = new Mock<IEndpointProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointProbeResult(EndpointState.Up, "HTTP 200", 12.3, null));
        return probe;
    }

    private static EndpointCheckOptions DailyCheck(string name) =>
        new() { Name = name, Target = "https://site.example", IntervalSeconds = 86_400 };

    [Fact]
    public async Task EndpointRenamedOnlyInCase_KeepsItsRow_UnderTheNewName()
    {
        var probe = UpProbe();
        var options = new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions { Endpoints = [DailyCheck("Site")] });
        using var worker = new EndpointMonitorWorker(probe.Object, _store, _alerts, options, _time, NullLogger<EndpointMonitorWorker>.Instance);

        await worker.PollOnceAsync(Ct);
        var checkedAt = _time.GetUtcNow().UtcDateTime;

        options.CurrentValue = new EndpointMonitorOptions { Endpoints = [DailyCheck("site")] };
        _time.Advance(EndpointMonitorWorker.Tick);
        await worker.PollOnceAsync(Ct);

        // The check is not due for a day; its last result is still reported, under the new name.
        probe.Verify(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.GetLatestEndpointStatuses().Should().ContainSingle()
            .Which.Should().Match<EndpointStatus>(e => e.Name == "site" && e.Status == "Up" && e.Timestamp == checkedAt);
    }

    [Fact]
    public async Task EndpointRemovedAndAddedAgain_RunsImmediately()
    {
        var probe = UpProbe();
        var options = new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions { Endpoints = [DailyCheck("site")] });
        using var worker = new EndpointMonitorWorker(probe.Object, _store, _alerts, options, _time, NullLogger<EndpointMonitorWorker>.Instance);
        await worker.PollOnceAsync(Ct);

        options.CurrentValue = new EndpointMonitorOptions();
        _time.Advance(EndpointMonitorWorker.Tick);
        await worker.PollOnceAsync(Ct);
        _store.GetLatestEndpointStatuses().Should().BeEmpty();

        options.CurrentValue = new EndpointMonitorOptions { Endpoints = [DailyCheck("SITE")] };
        _time.Advance(EndpointMonitorWorker.Tick);
        await worker.PollOnceAsync(Ct);

        probe.Verify(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _store.GetLatestEndpointStatuses().Select(e => e.Name).Should().Equal("SITE");
    }

    [Fact]
    public async Task EndpointRemovedFromConfiguration_DisappearsOnTheNextPoll()
    {
        var probe = new Mock<IEndpointProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointProbeResult(EndpointState.Up, "HTTP 200", 12.3, null));
        EndpointCheckOptions Check(string name) => new() { Name = name, Target = $"https://{name}.example", IntervalSeconds = 60 };
        var options = new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions { Endpoints = [Check("site"), Check("api")] });
        using var worker = new EndpointMonitorWorker(probe.Object, _store, _alerts, options, _time, NullLogger<EndpointMonitorWorker>.Instance);
        IEnumerable<string> Reported() => _store.GetLatestEndpointStatuses().Select(e => e.Name);

        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["site", "api"]);

        options.CurrentValue = new EndpointMonitorOptions { Endpoints = [Check("site")] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["site"]);

        // A check that becomes invalid is still configured: its row stays (marked, see below).
        options.CurrentValue = new EndpointMonitorOptions { Endpoints = [new EndpointCheckOptions { Name = "site", Target = "" }] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["site"]);

        options.CurrentValue = new EndpointMonitorOptions { Endpoints = [Check("api")] };
        await worker.PollOnceAsync(Ct);
        Reported().Should().BeEquivalentTo(["api"]);
    }

    [Fact]
    public async Task EndpointTemporarilyInvalid_KeepsItsLastResult_MarkedAsAConfigurationError()
    {
        var probe = new Mock<IEndpointProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointProbeResult(EndpointState.Down, "HTTP 503", 40, null));
        var options = new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions { Endpoints = [DailyCheck("site")] });
        using var worker = new EndpointMonitorWorker(probe.Object, _store, _alerts, options, _time, NullLogger<EndpointMonitorWorker>.Instance);
        await worker.PollOnceAsync(Ct);
        var last = _store.GetLatestEndpointStatuses().Should().ContainSingle().Subject;

        // Invalid (and renamed in case) for a while: the row is kept, with an explicit state.
        options.CurrentValue = new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "Site", Target = "not a url", IntervalSeconds = 86_400 }],
        };
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(EndpointMonitorWorker.Tick);
            await worker.PollOnceAsync(Ct);
        }

        var marked = _store.GetLatestEndpointStatuses().Should().ContainSingle().Subject;
        marked.Should().BeEquivalentTo(last with
        {
            Name = "Site",
            Status = EndpointStatus.ConfigurationError,
            Detail = "Configuration error: Monitoring:Endpoints:0:Target must be an absolute http(s) URL. Last result: Down: HTTP 503",
        });
        marked.IsUp.Should().BeFalse("the last result's reachability is kept");
        probe.Verify(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()), Times.Once);

        // A different error replaces the reason but keeps the last real result.
        options.CurrentValue = new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "Site", Target = "https://site.example", IntervalSeconds = 1 }],
        };
        _time.Advance(EndpointMonitorWorker.Tick);
        await worker.PollOnceAsync(Ct);
        _store.GetLatestEndpointStatuses().Single().Detail.Should().Be(
            "Configuration error: Monitoring:Endpoints:0:IntervalSeconds must be between 5 and 86400. Last result: Down: HTTP 503");

        // Fixed: it runs at once, although its interval is a day, and reports a real result again.
        options.CurrentValue = new EndpointMonitorOptions { Endpoints = [DailyCheck("Site")] };
        _time.Advance(EndpointMonitorWorker.Tick);
        await worker.PollOnceAsync(Ct);
        probe.Verify(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _store.GetLatestEndpointStatuses().Should().ContainSingle()
            .Which.Should().Match<EndpointStatus>(e => e.Name == "Site" && e.Status == "Down" && e.Detail == "HTTP 503");
    }

    [Fact]
    public async Task InvalidEndpointWithoutAResultYet_GetsNoRow()
    {
        var options = new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "new", Target = "" }],
        });
        using var worker = new EndpointMonitorWorker(UpProbe().Object, _store, _alerts, options, _time, NullLogger<EndpointMonitorWorker>.Instance);

        await worker.PollOnceAsync(Ct);

        _store.GetLatestEndpointStatuses().Should().BeEmpty();
    }
}