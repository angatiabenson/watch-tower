using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

/// <summary>
/// The daily report purge and the age-based backstop purge, with the real SQLite stores: history
/// goes, the current-state snapshot rows stay and keep being reported to the portal.
/// </summary>
public sealed class DailyPurgeTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly SqliteMetricsStore _metrics;
    private readonly SqliteOutboxStore _outbox;
    private readonly Mock<IPortalClient> _portal = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly List<MetricsBatchDto> _batches = new();

    public DailyPurgeTests()
    {
        _dir.Paths.EnsureDirectories();
        _metrics = new SqliteMetricsStore(_dir.Paths.DatabasePath);
        _outbox = new SqliteOutboxStore(_dir.Paths.DatabasePath);

        _portal.Setup(p => p.SendMetricsBatchAsync(It.IsAny<MetricsBatchDto>(), It.IsAny<CancellationToken>()))
            .Callback((MetricsBatchDto batch, CancellationToken _) => _batches.Add(batch))
            .ReturnsAsync(true);
        _email.Setup(e => e.IsConfigured).Returns(true);
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>()))
            .Returns(() => new ServerReport { GeneratedAt = Now, ReportPeriod = TimeSpan.FromHours(24) });
        _reportBuilder.Setup(r => r.RenderScheduledReportHtml(It.IsAny<ServerReport>())).Returns("<html/>");
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _outbox.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static IOptionsMonitor<T> Options<T>(T value)
    {
        var monitor = new Mock<IOptionsMonitor<T>>();
        monitor.Setup(m => m.CurrentValue).Returns(value);
        return monitor.Object;
    }

    private OutboxDispatchWorker OutboxWorker() => new(
        _portal.Object, _outbox, _outbox, _metrics, new MetricSnapshotTrigger(),
        Options(new PortalOptions { Enabled = true, AgentId = "agent" }), _time, NullLogger<OutboxDispatchWorker>.Instance);

    private EmailReporterWorker ReporterWorker() => new(
        _reportBuilder.Object, _email.Object, _metrics, _outbox,
        Options(new ReportingOptions { DailyReportTime = "08:00" }),
        Options(new DisplayOptions { Timezone = "Africa/Nairobi" }),
        Options(new PortalOptions { Enabled = true, AgentId = "agent" }),
        _time, _dir.Paths, NullLogger<EmailReporterWorker>.Instance);

    /// <summary>One row of every snapshot kind, last read <paramref name="readAt"/>.</summary>
    private void AddSnapshots(DateTime readAt)
    {
        _metrics.AddDiskMetric(new DiskMetric(readAt, "C:\\", 500, 200, 60));
        _metrics.AddDiskQueueMetric(new DiskQueueMetric(readAt, "0 C:", 0.5));
        _metrics.AddNetworkMetric(new NetworkMetric(readAt, "Ethernet", 0));
        _metrics.AddServiceStatus(new ServiceStatus(readAt, "W3SVC", "World Wide Web Publishing Service", "Running", true));
        _metrics.AddApplicationPoolStatus(new ApplicationPoolStatus(readAt, "DefaultAppPool", "Started", true));
        // Checked once a day: its next result can be almost a day away.
        _metrics.AddEndpointStatus(new EndpointStatus(readAt, "site", "Https", "https://example.com", "Up", true, 42));
    }

    private void SnapshotsShouldBeStored()
    {
        _metrics.GetLatestDiskMetrics().Should().ContainSingle(m => m.DriveName == "C:\\");
        _metrics.GetLatestDiskQueueMetrics().Should().ContainSingle(m => m.InstanceName == "0 C:");
        _metrics.GetLatestNetworkMetrics().Should().ContainSingle(m => m.InterfaceName == "Ethernet");
        _metrics.GetLatestServiceStatuses().Should().ContainSingle(m => m.ServiceName == "W3SVC");
        _metrics.GetLatestApplicationPoolStatuses().Should().ContainSingle(m => m.ApplicationPoolName == "DefaultAppPool");
        _metrics.GetLatestEndpointStatuses().Should().ContainSingle(m => m.Name == "site");
    }

    private static void ShouldReportEverySnapshot(MetricsBatchDto batch)
    {
        batch.DiskMetrics.Should().ContainSingle(m => m.DriveName == "C:\\");
        batch.DiskQueueMetrics.Should().ContainSingle(m => m.InstanceName == "0 C:");
        batch.NetworkMetrics.Should().ContainSingle(m => m.InterfaceName == "Ethernet");
        batch.ServiceStatuses.Should().ContainSingle(m => m.ServiceName == "W3SVC");
        batch.ApplicationPoolStatuses.Should().ContainSingle(m => m.ApplicationPoolName == "DefaultAppPool");
        batch.EndpointStatuses.Should().ContainSingle(m => m.Name == "site");
    }

    [Fact]
    public async Task DailyReportPurge_DeletesDeliveredHistory_AndKeepsReportingTheSnapshots()
    {
        AddSnapshots(Now.AddMinutes(-30));
        _metrics.AddCpuMetric(new CpuMetric(Now.AddMinutes(-20), 10));
        _metrics.AddRamMetric(new RamMetric(Now.AddMinutes(-20), 40, 6000, 10000));
        _metrics.AddIncident(new Incident(Now.AddMinutes(-20), IncidentType.DiskCritical, "in the report"));

        // The portal receives (and confirms) the history, then the daily report is sent and purges it.
        var outbox = OutboxWorker();
        await outbox.RunCycleAsync(CancellationToken.None);
        _batches.Should().ContainSingle().Which.CpuMetrics.Should().ContainSingle();
        _time.Advance(TimeSpan.FromMinutes(1));
        (await ReporterWorker().RunDailyReportAsync(CancellationToken.None)).Should().Be(EmailReporterWorker.DailyReportOutcome.Sent);

        _metrics.GetCpuMetrics(TimeSpan.FromDays(1)).Should().BeEmpty();
        _metrics.GetRamMetrics(TimeSpan.FromDays(1)).Should().BeEmpty();
        _metrics.GetIncidents(TimeSpan.FromDays(1)).Should().BeEmpty();
        SnapshotsShouldBeStored();

        // The next metrics batch still carries every resource, although none was read again.
        _time.Advance(TimeSpan.FromMinutes(1));
        await outbox.RunCycleAsync(CancellationToken.None);
        _batches.Should().HaveCount(2);
        _batches[1].CpuMetrics.Should().BeEmpty();
        ShouldReportEverySnapshot(_batches[1]);
    }

    [Fact]
    public async Task BackstopPurge_DeletesHistoryOlderThanTheRetention_AndKeepsOldSnapshots()
    {
        // Snapshot rows last read three days ago are still the current state of those resources.
        AddSnapshots(Now.AddDays(-3));
        _metrics.AddCpuMetric(new CpuMetric(Now.AddDays(-3), 10));
        _metrics.AddCpuMetric(new CpuMetric(Now.AddHours(-1), 11));
        _metrics.AddRamMetric(new RamMetric(Now.AddDays(-3), 40, 6000, 10000));
        _metrics.AddRamMetric(new RamMetric(Now.AddHours(-1), 41, 5900, 10000));
        _metrics.AddIncident(new Incident(Now.AddDays(-3), IncidentType.DiskCritical, "old"));
        _metrics.AddIncident(new Incident(Now.AddHours(-1), IncidentType.ServiceDown, "recent"));

        // Without SMTP the daily run only applies the 48 h age-based backstop purge.
        _email.Setup(e => e.IsConfigured).Returns(false);
        (await ReporterWorker().RunDailyReportAsync(CancellationToken.None))
            .Should().Be(EmailReporterWorker.DailyReportOutcome.EmailNotConfigured);

        _metrics.GetCpuMetrics(TimeSpan.FromDays(7)).Select(m => m.UsagePercent).Should().Equal(11);
        _metrics.GetRamMetrics(TimeSpan.FromDays(7)).Select(m => m.UsagePercent).Should().Equal(41);
        _metrics.GetIncidents(TimeSpan.FromDays(7)).Select(i => i.Description).Should().Equal("recent");
        SnapshotsShouldBeStored();

        await OutboxWorker().RunCycleAsync(CancellationToken.None);
        ShouldReportEverySnapshot(_batches.Should().ContainSingle().Subject);
    }

    [Fact]
    public void InMemoryStore_DailyPurge_KeepsTheSnapshots()
    {
        var store = new MetricsStore();
        var t = DateTime.UtcNow;
        store.AddCpuMetric(new CpuMetric(t, 10));
        store.AddDiskMetric(new DiskMetric(t, "C:\\", 500, 200, 60));
        store.AddEndpointStatus(new EndpointStatus(t, "site", "Https", "https://example.com", "Up", true));

        store.PurgeReportedData(long.MaxValue, long.MaxValue, t);

        store.GetCpuMetrics(TimeSpan.FromDays(1)).Should().BeEmpty();
        store.GetLatestDiskMetrics().Should().ContainSingle();
        store.GetLatestEndpointStatuses().Should().ContainSingle();
    }
}