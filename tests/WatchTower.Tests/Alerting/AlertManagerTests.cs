using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;

namespace WatchTower.Tests.Alerting;

public class AlertManagerTests
{
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IPortalEventPublisher> _portal = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private AlertManager Create(AlertingOptions? options = null, IAlertStateStore? state = null) =>
        TestAlerts.Create(_metricsStore.Object, _time, _dispatcher, options, _portal.Object, state);

    private Incident DiskIncident(string description = "Drive C:\\ at 91.0%") =>
        new(_time.GetUtcNow().UtcDateTime, IncidentType.DiskCritical, description);

    private static readonly AlertKey Key = AlertKey.Disk("C:\\");

    [Fact]
    public async Task FirstRaise_RecordsIncidentPublishesAndNotifies()
    {
        var alerts = Create();

        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical: C:\\", DiskIncident(), ct: TestContext.Current.CancellationToken);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.DiskCritical)), Times.Once);
        _portal.Verify(p => p.PublishAlertEventAsync(It.IsAny<Incident>(), "disk:C:\\", false, It.IsAny<CancellationToken>()), Times.Once);
        _dispatcher.Sent.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = AlertNotificationKind.Opened,
            AlertKey = "disk:C:\\",
            Severity = AlertSeverity.Critical,
            Title = "Disk Critical: C:\\",
            Server = "TEST-SRV",
            NotifyCount = 1,
        });
        alerts.IsOpen(Key).Should().BeTrue();
    }

    [Fact]
    public async Task RaiseWhileOpen_IsQuietUntilRenotifyInterval_ThenReminds()
    {
        var alerts = Create(new AlertingOptions { RenotifyMinutes = 60 });
        var factoryCalls = 0;
        Task<Incident> Factory(CancellationToken _)
        {
            factoryCalls++;
            return Task.FromResult(DiskIncident($"Drive C:\\ at {90 + factoryCalls}%"));
        }

        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Factory, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(59));
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Factory, TestContext.Current.CancellationToken);
        factoryCalls.Should().Be(1, "nothing is recorded or sent, so the incident is not even built");

        _time.Advance(TimeSpan.FromMinutes(1));
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Factory, TestContext.Current.CancellationToken);

        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Once);
        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Reminder);
        var reminder = _dispatcher.Sent[1];
        reminder.NotifyCount.Should().Be(2);
        reminder.Duration.Should().Be(TimeSpan.FromHours(1));
        reminder.Description.Should().Be("Drive C:\\ at 92%");
    }

    [Fact]
    public async Task RenotifyZero_NeverReminds()
    {
        var alerts = Create(new AlertingOptions { RenotifyMinutes = 0 });

        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromDays(3));
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);

        _dispatcher.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Resolve_SendsResolvedWithDurationAndPublishesResolvedEvent()
    {
        var alerts = Create();
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical: C:\\", DiskIncident(), ct: TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(42));

        var resolved = await alerts.ResolveAsync(Key, ct: TestContext.Current.CancellationToken);

        resolved.Should().BeTrue();
        alerts.IsOpen(Key).Should().BeFalse();
        var notification = _dispatcher.Sent[^1];
        notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        notification.Duration.Should().Be(TimeSpan.FromMinutes(42));
        notification.Description.Should().Contain("42 min");
        _portal.Verify(p => p.PublishAlertEventAsync(
            It.Is<Incident>(i => i.Type == IncidentType.DiskCritical), "disk:C:\\", true, It.IsAny<CancellationToken>()), Times.Once);
        // The resolution of a metric alert is not recorded locally (it would read as a new breach).
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Once);

        (await alerts.ResolveAsync(Key, ct: TestContext.Current.CancellationToken)).Should().BeFalse("it is no longer open");
        _dispatcher.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Resolve_WithSendRecoveryOff_DoesNotNotify()
    {
        var alerts = Create(new AlertingOptions { SendRecovery = false });
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);

        await alerts.ResolveAsync(Key, ct: TestContext.Current.CancellationToken);

        _dispatcher.Sent.Should().ContainSingle().Which.Kind.Should().Be(AlertNotificationKind.Opened);
    }

    [Fact]
    public async Task Resolve_RecordsRecoveryIncidentOnlyWhenAnAlertWasOpen()
    {
        var alerts = Create();
        var key = AlertKey.Service("W3SVC");
        var restarted = new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.ServiceRestarted, "restarted");

        (await alerts.ResolveAsync(key, restarted, TestContext.Current.CancellationToken)).Should().BeFalse();
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);

        await alerts.RaiseAsync(key, AlertSeverity.Critical, "Service Down: W3SVC", new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.ServiceRestartFailed, "failed"), ct: TestContext.Current.CancellationToken);
        (await alerts.ResolveAsync(key, restarted, TestContext.Current.CancellationToken)).Should().BeTrue();

        _metricsStore.Verify(s => s.AddIncident(restarted), Times.Once);
        _portal.Verify(p => p.PublishAlertEventAsync(restarted, "svc:W3SVC", true, It.IsAny<CancellationToken>()), Times.Once);
        _dispatcher.Sent[^1].Description.Should().Be("restarted");
    }

    [Fact]
    public async Task Escalation_NotifiesAgainAsOpened()
    {
        var alerts = Create();
        var key = AlertKey.Endpoint("site");

        await alerts.RaiseAsync(key, AlertSeverity.Warning, "Certificate expiring", new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.DiskQueueHigh, "expiring"), ct: TestContext.Current.CancellationToken);
        await alerts.RaiseAsync(key, AlertSeverity.Warning, "Certificate expiring", new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.DiskQueueHigh, "expiring"), ct: TestContext.Current.CancellationToken);
        await alerts.RaiseAsync(key, AlertSeverity.Critical, "Endpoint down", new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.DiskCritical, "down"), ct: TestContext.Current.CancellationToken);

        _dispatcher.Sent.Select(n => (n.Kind, n.Severity)).Should().Equal(
            (AlertNotificationKind.Opened, AlertSeverity.Warning),
            (AlertNotificationKind.Opened, AlertSeverity.Critical));
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Exactly(2));
        alerts.GetOpenAlerts().Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.Critical);
    }

    [Fact]
    public async Task NoChannelConfigured_AlertStaysPendingAndNotifiesOnceAChannelExists()
    {
        var alerts = Create();
        _dispatcher.Channels = 0;
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);

        _dispatcher.Channels = 1;
        _time.Advance(TimeSpan.FromMinutes(1));
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);

        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Opened);
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Once);
    }

    [Fact]
    public async Task NeverNotifiedAlert_ResolvesSilently()
    {
        var alerts = Create();
        _dispatcher.Channels = 0;
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);
        _dispatcher.Channels = 1;

        await alerts.ResolveAsync(Key, ct: TestContext.Current.CancellationToken);

        _dispatcher.Sent.Where(n => n.Kind == AlertNotificationKind.Resolved).Should().BeEmpty();
    }

    [Fact]
    public async Task DispatcherFailure_DoesNotPropagate()
    {
        var dispatcher = new Mock<INotificationDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<AlertNotification>(), It.IsAny<IReadOnlySet<string>?>(),
                It.IsAny<Action<ChannelDelivery>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var alerts = TestAlerts.Create(_metricsStore.Object, _time, dispatcher.Object);

        var act = () => alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident());

        await act.Should().NotThrowAsync();
        alerts.IsOpen(Key).Should().BeTrue();
    }

    [Fact]
    public async Task OneShotEvent_IsRecordedPublishedAndNotifiedWithoutState()
    {
        var alerts = Create();
        var incident = new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.ServiceDown, "Server restarted");

        await alerts.NotifyEventAsync(AlertKey.Reboot, AlertSeverity.Warning, "Server restarted", incident, TestContext.Current.CancellationToken);

        _metricsStore.Verify(s => s.AddIncident(incident), Times.Once);
        _portal.Verify(p => p.PublishAlertEventAsync(incident, "reboot", null, It.IsAny<CancellationToken>()), Times.Once);
        _dispatcher.Sent.Should().ContainSingle().Which.Kind.Should().Be(AlertNotificationKind.Event);
        alerts.GetOpenAlerts().Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAlert_SurvivesRestart_AndIsNotResent()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var dbPath = dir.Paths.DatabasePath;

        var before = Create(state: new SqliteAlertStateStore(dbPath));
        await before.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));

        // New process: fresh manager over the same database.
        var after = Create(state: new SqliteAlertStateStore(dbPath));
        await after.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);
        after.IsOpen(Key).Should().BeTrue();
        _dispatcher.Sent.Should().ContainSingle("the alert was already notified before the restart");

        await after.ResolveAsync(Key, ct: TestContext.Current.CancellationToken);
        _dispatcher.Sent[^1].Kind.Should().Be(AlertNotificationKind.Resolved);
        _dispatcher.Sent[^1].Duration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task AlertState_IsNotClearedByTheDailyReportPurge()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var metrics = new SqliteMetricsStore(dir.Paths.DatabasePath);
        var state = new SqliteAlertStateStore(dir.Paths.DatabasePath);
        var alerts = TestAlerts.Create(metrics, _time, _dispatcher, state: state);
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", DiskIncident(), ct: TestContext.Current.CancellationToken);

        metrics.PurgeReportedData(long.MaxValue, long.MaxValue, DateTime.UtcNow.AddDays(1));

        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadAll()
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                Key = "disk:C:\\",
                Severity = AlertSeverity.Critical,
                IncidentType = IncidentType.DiskCritical,
                NotifyCount = 1,
                ResolvedAt = (DateTime?)null,
            });
    }

    [Fact]
    public void SqliteAlertStateStore_RoundTripsAgentValues()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var store = new SqliteAlertStateStore(dir.Paths.DatabasePath);

        store.GetValue("last_boot").Should().BeNull();
        store.SetValue("last_boot", "a");
        store.SetValue("last_boot", "b");

        new SqliteAlertStateStore(dir.Paths.DatabasePath).GetValue("last_boot").Should().Be("b");
    }

    [Fact]
    public void RecoveryDebouncer_RequiresAnUninterruptedClearWindow()
    {
        var debouncer = new RecoveryDebouncer();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(5);

        debouncer.Clear(Key, t0, window).Should().BeFalse();
        debouncer.Clear(Key, t0.AddMinutes(4), window).Should().BeFalse();
        debouncer.Breached(Key);
        debouncer.Clear(Key, t0.AddMinutes(6), window).Should().BeFalse("the breach restarted the window");
        debouncer.Clear(Key, t0.AddMinutes(11), window).Should().BeTrue();
        debouncer.Clear(AlertKey.Cpu, t0, TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public async Task PortalEventPublisher_SerializesAlertKeyAndResolved()
    {
        var outbox = new Mock<IOutboxStore>();
        var payloads = new List<string>();
        outbox.Setup(o => o.AddMessageAsync("AgentEvent", Capture.In(payloads), 1, It.IsAny<long?>(), It.IsAny<long?>()))
            .Returns(Task.CompletedTask);
        var publisher = new PortalEventPublisher(outbox.Object, NullLogger<PortalEventPublisher>.Instance);
        var incident = new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.DiskCritical, "full");

        await publisher.PublishIncidentAsync(incident, CancellationToken.None);
        await publisher.PublishAlertEventAsync(incident, "disk:C:\\", resolved: true, CancellationToken.None);

        var plain = JsonDocument.Parse(payloads[0]).RootElement;
        plain.TryGetProperty("alertKey", out _).Should().BeFalse();
        plain.TryGetProperty("resolved", out _).Should().BeFalse();
        var alert = JsonDocument.Parse(payloads[1]).RootElement;
        alert.GetProperty("alertKey").GetString().Should().Be("disk:C:\\");
        alert.GetProperty("resolved").GetBoolean().Should().BeTrue();
        alert.GetProperty("incidentType").GetInt32().Should().Be((int)IncidentType.DiskCritical);
    }
}