using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Tests.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

/// <summary>
/// A snapshot entry keeps its last reading and timestamp until its resource is removed; the
/// metrics batch marks it <c>isStale</c> once that reading is older than its freshness window, and
/// the operator gets one warning saying why.
/// </summary>
public sealed class SnapshotStalenessTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_stale_{Guid.NewGuid()}.db");
    private readonly SqliteMetricsStore _store;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-04T00:00:00Z"));
    private readonly Mock<ISystemMetricsProvider> _provider = new();
    private readonly RecordingLoggerProvider _logs = new();

    // D:\ is listed by the inventory but only readable while _dReady is true (a fixed drive that goes not-ready).
    private bool _dReady = true;

    public SnapshotStalenessTests()
    {
        _store = new SqliteMetricsStore(_dbPath);
        DateTime Now() => _time.GetUtcNow().UtcDateTime;
        _provider.Setup(p => p.GetDiskMetrics()).Returns(() =>
            new[] { "C:\\", "D:\\" }.Where(d => d != "D:\\" || _dReady).Select(d => new DiskMetric(Now(), d, 500, 400, 20)).ToList());
        _provider.Setup(p => p.GetDiskQueueMetrics()).Returns(() => [new DiskQueueMetric(Now(), "0 C:", 0)]);
        _provider.Setup(p => p.GetDiskInventory()).Returns(() => new ResourceInventory(["C:\\", "D:\\"]));
        _provider.Setup(p => p.GetDiskQueueInventory()).Returns(() => new ResourceInventory(["0 C:"]));
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

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private DiskMonitorWorker DiskWorker() => new(
        _provider.Object, _store, TestAlerts.Create(_store, _time, new RecordingDispatcher()),
        new TestOptionsMonitor<DiskMonitorOptions>(new DiskMonitorOptions { PollIntervalSeconds = 60 }),
        _time, NullLogger<DiskMonitorWorker>.Instance);

    private StaleSnapshotWorker StaleWorker(SnapshotFreshnessPolicy? policy = null) =>
        new(_store, policy ?? SnapshotFreshnessPolicy.Default, _time, _logs.CreateLogger<StaleSnapshotWorker>());

    /// <summary>Builds the next metrics batch exactly as the outbox worker does.</summary>
    private async Task<MetricsBatchDto> NextBatch(SnapshotFreshnessPolicy? policy = null)
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
            new TestOptionsMonitor<PortalOptions>(new PortalOptions { Enabled = true }), _time, NullLogger<OutboxDispatchWorker>.Instance,
            freshness: policy);

        await worker.RunCycleAsync(Ct);

        payload.Should().NotBeNull();
        return JsonSerializer.Deserialize<MetricsBatchDto>(payload!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static DiskMetricDto Drive(MetricsBatchDto batch, string name) => batch.DiskMetrics.Single(d => d.DriveName == name);

    private IEnumerable<LogEntry> StaleWarnings => _logs.At(LogLevel.Warning).Where(e => e.Message.Contains("marked stale"));

    [Fact]
    public async Task AReading100DaysOld_SurvivesBothPurges_AndIsSentWithItsTimestamp_MarkedStale()
    {
        var old = Now - TimeSpan.FromDays(100);
        _store.AddDiskMetric(new DiskMetric(old, "D:\\", 500, 123, 75.4));
        _store.AddDiskMetric(new DiskMetric(Now - TimeSpan.FromMinutes(1), "C:\\", 500, 400, 20));
        _store.AddCpuMetric(new CpuMetric(old, 10));

        // The daily report purge and the retention purge only delete history.
        _store.PurgeReportedData(long.MaxValue, long.MaxValue, Now);
        _store.Purge(TimeSpan.FromDays(7));

        var batch = await NextBatch();

        var d = Drive(batch, "D:\\");
        d.Should().Be(new DiskMetricDto(old.ToString("O"), "D:\\", 500, 123, 75.4, IsStale: true), "the last reading and its timestamp are unchanged");
        Drive(batch, "C:\\").IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task ADriveThatGoesNotReady_TurnsStaleAfterItsWindow_AndFreshWhenReadAgain()
    {
        var worker = DiskWorker();
        await worker.PollOnceAsync(Ct);
        var lastGood = Now;

        _dReady = false; // still in the inventory, no longer readable
        for (var minute = 1; minute <= 10; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await worker.PollOnceAsync(Ct);
        }

        var atTheEdge = await NextBatch();
        Drive(atTheEdge, "D:\\").IsStale.Should().BeFalse("exactly 10 min old is still inside max(3 x 60 s, 10 min)");

        _time.Advance(TimeSpan.FromMinutes(1));
        await worker.PollOnceAsync(Ct);
        var stale = await NextBatch();
        Drive(stale, "D:\\").Should().Be(new DiskMetricDto(lastGood.ToString("O"), "D:\\", 500, 400, 20, IsStale: true));
        Drive(stale, "C:\\").IsStale.Should().BeFalse();
        stale.DiskQueueMetrics.Should().ContainSingle().Which.IsStale.Should().BeFalse();

        _dReady = true;
        _time.Advance(TimeSpan.FromMinutes(1));
        await worker.PollOnceAsync(Ct);
        var fresh = await NextBatch();
        Drive(fresh, "D:\\").Should().Be(new DiskMetricDto(Now.ToString("O"), "D:\\", 500, 400, 20, IsStale: false));
    }

    [Fact]
    public async Task EveryKind_UsesItsOwnWindow_ConfigurationErrorEndpointsIncluded()
    {
        var policy = new SnapshotFreshnessPolicy(
            new TestOptionsMonitor<DiskMonitorOptions>(new()),
            new TestOptionsMonitor<NetworkMonitorOptions>(new NetworkMonitorOptions { PollIntervalSeconds = 600 }), // 30 min
            new TestOptionsMonitor<ServiceMonitorOptions>(new()),
            new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new()),
            new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions
            {
                Endpoints =
                [
                    new EndpointCheckOptions { Name = "daily", IntervalSeconds = 86_400, TimeoutSeconds = 30 }, // 48 h 30 s
                    new EndpointCheckOptions { Name = "broken", Target = "not a url" },                         // 10 min
                ],
            }));
        var ago = (double minutes) => Now - TimeSpan.FromMinutes(minutes);
        _store.AddDiskQueueMetric(new DiskQueueMetric(ago(11), "0 C:", 1));
        _store.AddNetworkMetric(new NetworkMetric(ago(20), "Ethernet", 0));
        _store.AddNetworkMetric(new NetworkMetric(ago(31), "USB Ethernet", 0));
        _store.AddServiceStatus(new ServiceStatus(ago(11), "W3SVC", "World Wide Web Publishing Service", "Running", true));
        _store.AddServiceStatus(new ServiceStatus(ago(1), "Spooler", "Print Spooler", "Stopped", false));
        _store.AddApplicationPoolStatus(new ApplicationPoolStatus(ago(9), "DefaultAppPool", "Started", true));
        _store.AddEndpointStatus(new EndpointStatus(ago(47 * 60), "daily", "Certificate", "example.com:443", "Up", true));
        _store.AddEndpointStatus(new EndpointStatus(ago(30), "broken", "Http", "https://old.example.com/",
            EndpointStatus.ConfigurationError, false, 12, null, "Configuration error: x Last result: Down: HTTP 503"));

        var batch = await NextBatch(policy);

        batch.DiskQueueMetrics.Single().IsStale.Should().BeTrue();
        batch.NetworkMetrics.ToDictionary(n => n.InterfaceName, n => n.IsStale)
            .Should().BeEquivalentTo(new Dictionary<string, bool?> { ["Ethernet"] = false, ["USB Ethernet"] = true });
        batch.ServiceStatuses.ToDictionary(s => s.ServiceName, s => s.IsStale)
            .Should().BeEquivalentTo(new Dictionary<string, bool?> { ["W3SVC"] = true, ["Spooler"] = false });
        batch.ApplicationPoolStatuses.Single().IsStale.Should().BeFalse();
        var endpoints = batch.EndpointStatuses!.ToDictionary(e => e.Name);
        endpoints["daily"].IsStale.Should().BeFalse("a daily check is fresh for 2 x 24 h + its timeout");
        endpoints["broken"].Should().Be(new EndpointStatusDto(ago(30).ToString("O"), "broken", "Http", "https://old.example.com/",
            EndpointStatus.ConfigurationError, false, 12, null, "Configuration error: x Last result: Down: HTTP 503", IsStale: true),
            "a ConfigurationError entry keeps its status and last result and gets isStale like any other");
    }

    [Fact]
    public async Task AStaleDrive_IsLoggedOnceAsAWarning_ThenOnceAsReadAgain()
    {
        var worker = DiskWorker();
        var stale = StaleWorker();
        await worker.PollOnceAsync(Ct);
        _dReady = false;

        for (var minute = 1; minute <= 60; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await worker.PollOnceAsync(Ct);
            stale.CheckOnce();
        }

        var warning = StaleWarnings.Should().ContainSingle("one warning when D:\\ first becomes stale, not one per check").Subject;
        warning.Message.Should().Contain(@"Drive D:\").And.Contain("freshness window 10 min").And.Contain("not ready");
        StaleWarnings.Should().NotContain(e => e.Message.Contains(@"C:\"), "C:\\ is read on every poll");

        _dReady = true;
        _time.Advance(TimeSpan.FromMinutes(1));
        await worker.PollOnceAsync(Ct);
        stale.CheckOnce();
        stale.CheckOnce();

        _logs.At(LogLevel.Information).Where(e => e.Message.Contains("read again")).Should().ContainSingle()
            .Which.Message.Should().Contain(@"Drive D:\");
        StaleWarnings.Should().HaveCount(1);
    }

    [Fact]
    public async Task AResourceThatKeepsGoingStale_IsWarnedAboutAtMostOncePerHour()
    {
        var worker = DiskWorker();
        var stale = StaleWorker();
        await worker.PollOnceAsync(Ct);

        // Stale for a while, read again, stale again: three episodes within the hour.
        async Task Episode()
        {
            _dReady = false;
            for (var minute = 0; minute < 12; minute++)
            {
                _time.Advance(TimeSpan.FromMinutes(1));
                await worker.PollOnceAsync(Ct);
                stale.CheckOnce();
            }
            _dReady = true;
            _time.Advance(TimeSpan.FromMinutes(1));
            await worker.PollOnceAsync(Ct);
            stale.CheckOnce();
        }

        await Episode();
        await Episode();
        await Episode();
        StaleWarnings.Should().ContainSingle("the next episodes are inside the hour");

        // After the hour the next episode is warned about again, with the ones that were not.
        _time.Advance(TimeSpan.FromMinutes(30));
        await worker.PollOnceAsync(Ct);
        await Episode();

        StaleWarnings.Should().HaveCount(2);
        StaleWarnings.Last().Message.Should().Contain("2 shorter stale episode(s) since the last warning were not logged");
    }

    [Fact]
    public void AnEntryThatStaysStaleThroughTheRateLimit_IsWarnedAboutWhenTheRateLimitEnds()
    {
        var stale = StaleWorker();
        _store.AddDiskMetric(new DiskMetric(Now - TimeSpan.FromMinutes(11), "D:\\", 500, 400, 20));
        stale.CheckOnce();
        _store.AddDiskMetric(new DiskMetric(Now, "D:\\", 500, 400, 20)); // read again
        stale.CheckOnce();
        _time.Advance(TimeSpan.FromMinutes(11)); // stale again, 11 min after the warning
        stale.CheckOnce();
        StaleWarnings.Should().HaveCount(1);

        _time.Advance(TimeSpan.FromMinutes(49)); // the hour has passed, still stale
        stale.CheckOnce();
        stale.CheckOnce();

        StaleWarnings.Should().HaveCount(2, "an episode that outlasts the rate limit is logged once it allows");
    }

    [Fact]
    public void AStaleConfigurationErrorEndpoint_SaysTheConfigurationMustBeFixed()
    {
        _store.AddEndpointStatus(new EndpointStatus(Now - TimeSpan.FromMinutes(15), "sql-port", "Tcp", "sql01:1433",
            EndpointStatus.ConfigurationError, true, 3, null, "Configuration error: x Last result: Up"));

        StaleWorker().CheckOnce();

        StaleWarnings.Should().ContainSingle().Which.Message.Should().Contain("Endpoint check sql-port").And.Contain("fix its configuration");
    }

    [Fact]
    public void ARemovedResource_IsForgotten_AndFreshEntriesAreNeverLogged()
    {
        var stale = StaleWorker();
        _store.AddServiceStatus(new ServiceStatus(Now, "W3SVC", "World Wide Web Publishing Service", "Running", true));
        _store.AddServiceStatus(new ServiceStatus(Now - TimeSpan.FromMinutes(30), "Old", "Old", "Stopped", false));
        stale.CheckOnce();
        StaleWarnings.Should().ContainSingle().Which.Message.Should().Contain("Service Old");

        _store.RemoveSnapshotsExcept(SnapshotKind.Service, ["W3SVC"]);
        stale.CheckOnce();
        _time.Advance(TimeSpan.FromSeconds(30));
        _store.AddServiceStatus(new ServiceStatus(Now, "W3SVC", "World Wide Web Publishing Service", "Running", true));
        stale.CheckOnce();

        StaleWarnings.Should().HaveCount(1);
        _logs.At(LogLevel.Information).Should().BeEmpty("a removed resource is not 'read again'");
    }
}