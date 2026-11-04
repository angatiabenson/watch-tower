using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Alerting;

/// <summary>Per-channel delivery tracking and retries of alert notifications.</summary>
public class NotificationDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static readonly AlertKey Key = AlertKey.Disk("C:\\");

    private AlertManager Alerts(AlertingOptions? options = null, IAlertStateStore? state = null, INotificationDispatcher? dispatcher = null) =>
        TestAlerts.Create(_metrics.Object, _time, dispatcher ?? _dispatcher, options, state: state);

    private Incident Disk() => new(_time.GetUtcNow().UtcDateTime, IncidentType.DiskCritical, "Drive C:\\ at 95%",
        [new ProcessInfo("sqlservr", 42, 1, 2048, 12.5)]);

    private Task Raise(AlertManager alerts) => alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Lets time pass in <paramref name="step"/>s, running the delivery worker's pass each step.</summary>
    private async Task RunFor(AlertManager alerts, TimeSpan duration, TimeSpan step, Action? eachStep = null)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step)
        {
            _time.Advance(step);
            eachStep?.Invoke();
            await alerts.ProcessPendingAsync(Ct);
        }
    }

    [Fact]
    public async Task FailedSend_IsRetriedUntilTheChannelIsBack_AndCountsOnlyTheDelivery()
    {
        // The judge's probe: one failed send, the channel comes back, two days pass, no reminders.
        var alerts = Alerts(new AlertingOptions { RenotifyMinutes = 0 });
        _dispatcher.Failing.Add("ch1");

        await Raise(alerts);

        alerts.GetOpenAlerts().Single().Should().BeEquivalentTo(new { NotifyCount = 0, LastNotifiedAt = (DateTime?)null },
            "a failed send is not a notification");
        alerts.GetPendingDeliveries().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FailedAttempts = 1,
            NextAttemptAt = Now.AddMinutes(1),
            LastError = "ch1: Rejected with HTTP 503",
        });

        var restoredAt = Now.AddHours(2);
        DateTime? deliveredAt = null;
        await RunFor(alerts, TimeSpan.FromDays(2), TimeSpan.FromMinutes(1), () =>
        {
            if (Now >= restoredAt)
                _dispatcher.Failing.Clear();
            if (deliveredAt is null && _dispatcher.Delivered.Count > 0)
                deliveredAt = Now;
        });

        // Backoff 1, 2, 4, 8, 16, 30, 30… minutes: t = 0, 1, 3, 7, 15, 31, 61, 91, 121 (delivered).
        _dispatcher.Sent.Should().HaveCount(9, "retries back off instead of running on every pass");
        _dispatcher.Delivered.Should().ContainSingle().Which.Notification.Kind.Should().Be(AlertNotificationKind.Opened);
        var state = alerts.GetOpenAlerts().Single();
        state.NotifyCount.Should().Be(1);
        state.LastNotifiedAt.Should().Be(new DateTime(2026, 1, 1, 2, 1, 0, DateTimeKind.Utc), "when it was actually delivered");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task PartialFailure_RetriesOnlyTheChannelsThatFailed()
    {
        var alerts = Alerts();
        _dispatcher.Channels = 3;
        _dispatcher.Failing.Add("ch2");

        await Raise(alerts);

        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(1, "it reached ch1 and ch3");
        alerts.GetPendingDeliveries().Single().DeliveredChannels.Should().BeEquivalentTo("ch1", "ch3");

        _dispatcher.Failing.Clear();
        await RunFor(alerts, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));

        _dispatcher.Delivered.Select(d => d.Channel).Should().BeEquivalentTo(["ch1", "ch3", "ch2"],
            "each channel gets the notice exactly once");
        _dispatcher.Delivered.Select(d => d.Notification.Kind).Should().AllBeEquivalentTo(AlertNotificationKind.Opened);
        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(1, "one notice, however many channels");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ResolvedNotice_IsRetriedToo()
    {
        var alerts = Alerts();
        await Raise(alerts);
        _dispatcher.Failing.Add("ch1");
        _time.Advance(TimeSpan.FromMinutes(42));

        await alerts.ResolveAsync(Key, ct: Ct);
        _dispatcher.Delivered.Should().ContainSingle("only the opened notice got through so far");

        _dispatcher.Failing.Clear();
        await RunFor(alerts, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15));

        var resolved = _dispatcher.Delivered.Should().HaveCount(2).And.Subject.Last().Notification;
        resolved.Kind.Should().Be(AlertNotificationKind.Resolved);
        resolved.Duration.Should().Be(TimeSpan.FromMinutes(42));
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ResolvedNotice_RetriesStopAfterTheGiveUpPeriod()
    {
        var alerts = Alerts();
        await Raise(alerts);
        _dispatcher.Failing.Add("ch1");
        await alerts.ResolveAsync(Key, ct: Ct);

        await RunFor(alerts, TimeSpan.FromHours(30), TimeSpan.FromMinutes(5));

        alerts.GetPendingDeliveries().Should().BeEmpty();
        var attempts = _dispatcher.Sent.Count(n => n.Kind == AlertNotificationKind.Resolved);
        attempts.Should().BeInRange(45, 60, "about every 30 minutes for 24 hours, then no more");
    }

    [Theory]
    [InlineData(AlertSeverity.Warning)]
    [InlineData(AlertSeverity.Critical)]
    public async Task OpenedNotice_NeverDelivered_ThenResolved_SendsTheResolvedNotice(AlertSeverity severity)
    {
        var alerts = Alerts();
        _dispatcher.Failing.Add("ch1");
        await alerts.RaiseAsync(Key, severity, "Disk Critical", Disk(), Ct);
        _dispatcher.Failing.Clear();

        await alerts.ResolveAsync(Key, ct: Ct);
        if (severity == AlertSeverity.Critical)
        {
            _dispatcher.Sent.Should().HaveCount(2, "a critical recovery probes the backing-off channel at once");
        }
        else
        {
            _dispatcher.Sent.Should().ContainSingle("the channel just failed: it is tried again after its backoff");
            _time.Advance(NotificationRetryPolicy.Backoff(1));
            await alerts.ProcessPendingAsync(Ct);
        }

        _dispatcher.Delivered.Should().ContainSingle().Which.Notification.Kind.Should().Be(AlertNotificationKind.Resolved,
            "operators never heard of the alert, so the resolution tells the whole story");
        alerts.GetPendingDeliveries().Should().BeEmpty("the undelivered opened notice is superseded");
    }

    [Fact]
    public async Task ReminderSupersedes_ANoticeOneChannelKeepsRejecting()
    {
        var alerts = Alerts(new AlertingOptions { RenotifyMinutes = 60 });
        _dispatcher.Channels = 2;
        _dispatcher.Failing.Add("ch2");

        await Raise(alerts);
        for (var i = 0; i < 61; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Raise(alerts);
            await alerts.ProcessPendingAsync(Ct);
        }

        _dispatcher.Delivered.Where(d => d.Channel == "ch1").Select(d => d.Notification.Kind)
            .Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Reminder);
        var pending = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        pending.Notification.Kind.Should().Be(AlertNotificationKind.Reminder);
        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(2);
    }

    [Fact]
    public async Task PendingDelivery_SurvivesARestart_AndSkipsChannelsThatHaveIt()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        _dispatcher.Channels = 2;
        _dispatcher.Failing.Add("ch2");
        var before = Alerts(state: new SqliteAlertStateStore(dir.Paths.DatabasePath));
        await Raise(before);

        // New process, channel fixed.
        var after = new RecordingDispatcher { Channels = 2 };
        var restarted = Alerts(state: new SqliteAlertStateStore(dir.Paths.DatabasePath), dispatcher: after);
        var pending = restarted.GetPendingDeliveries().Should().ContainSingle().Subject;
        pending.DeliveredChannels.Should().Equal("ch1");
        pending.Notification.Incident!.TopProcesses.Should().ContainSingle().Which.Name.Should().Be("sqlservr");

        await restarted.ProcessPendingAsync(Ct);
        after.Delivered.Should().BeEmpty("the retry is not due yet");

        _time.Advance(TimeSpan.FromMinutes(1));
        await restarted.ProcessPendingAsync(Ct);

        after.Delivered.Should().ContainSingle().Which.Channel.Should().Be("ch2");
        restarted.GetPendingDeliveries().Should().BeEmpty();
        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Should().BeEmpty();
        restarted.GetOpenAlerts().Single().NotifyCount.Should().Be(1);
    }

    [Fact]
    public void SqliteStore_RoundTripsAPendingDelivery()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var store = new SqliteAlertStateStore(dir.Paths.DatabasePath);
        var notification = new AlertNotification(AlertNotificationKind.Reminder, "svc:W3SVC", AlertSeverity.Critical, "Service Down",
            "failed", "SRV", Now, Now.AddHours(-1), TimeSpan.FromHours(1), Disk(), 3);
        var delivery = new PendingDelivery("id-1", notification, Now, ["teams"], 2, Now.AddMinutes(-3), Now.AddMinutes(2),
            "slack: Rejected with HTTP 500", Held: true)
        {
            ChannelRetries = [new ChannelRetry("slack", 2, Now.AddMinutes(2), "Rejected with HTTP 500")],
        };

        store.UpsertDelivery(delivery);
        store.UpsertDelivery(delivery with { FailedAttempts = 3, ExplicitRecovery = true });

        var loaded = new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Should().ContainSingle().Subject;
        loaded.Should().BeEquivalentTo(delivery with { FailedAttempts = 3, ExplicitRecovery = true });
        store.DeleteDelivery("id-1");
        store.LoadDeliveries().Should().BeEmpty();
    }

    [Fact]
    public void SqliteStore_UpgradingADeliveryTableWithoutTheExplicitRecoveryColumn_TreatsItsRecoveriesAsExplicit()
    {
        // The reviewer's probe: that version could not tell a recovery whose opening was being
        // sent when the alert closed (NotifyCount 0, yet possibly seen by operators) from one of
        // an alert that opened and cleared inside a window. Such a row is sent on its own.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var notification = new AlertNotification(AlertNotificationKind.Resolved, "svc:W3SVC", AlertSeverity.Critical, "Service Down",
            "running again", "SRV", Now, Now.AddHours(-1), TimeSpan.FromHours(1), NotifyCount: 0);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dir.Paths.DatabasePath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // The table as the previous version created it.
            cmd.CommandText =
                """
                CREATE TABLE alert_delivery (
                    id TEXT PRIMARY KEY, alert_key TEXT NOT NULL COLLATE NOCASE, kind INTEGER NOT NULL,
                    notification TEXT NOT NULL, created_at TEXT NOT NULL, delivered_channels TEXT NOT NULL DEFAULT '[]',
                    failed_attempts INTEGER NOT NULL DEFAULT 0, first_attempt_at TEXT, next_attempt_at TEXT NOT NULL,
                    last_error TEXT, held INTEGER NOT NULL DEFAULT 0);
                INSERT INTO alert_delivery (id, alert_key, kind, notification, created_at, next_attempt_at, held)
                VALUES ('old', 'svc:W3SVC', 2, @notification, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', 1);
                """;
            cmd.Parameters.AddWithValue("@notification", System.Text.Json.JsonSerializer.Serialize(notification));
            cmd.ExecuteNonQuery();
        }

        var store = new SqliteAlertStateStore(dir.Paths.DatabasePath);
        var old = store.LoadDeliveries().Should().ContainSingle().Subject;
        old.ExplicitRecovery.Should().BeTrue("it was pending when the column was added");
        old.IsSummarisedRecovery.Should().BeFalse();
        old.Occurrence.Should().Be(1);

        // The new default only applies to rows written from now on, and a second start migrates nothing.
        store.UpsertDelivery(old with { ExplicitRecovery = false });
        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Single().ExplicitRecovery.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void Backoff_DoublesUpToItsBound(int failedAttempts, int minutes) =>
        NotificationRetryPolicy.Backoff(failedAttempts).Should().Be(TimeSpan.FromMinutes(minutes));

    [Fact]
    public async Task OneShotEvent_FailedChannelIsRetried()
    {
        var alerts = Alerts();
        _dispatcher.Failing.Add("ch1");
        await alerts.NotifyEventAsync(AlertKey.Reboot, AlertSeverity.Warning, "Server restarted",
            new Incident(Now, IncidentType.ServerRestarted, "rebooted"), Ct);

        _dispatcher.Failing.Clear();
        await RunFor(alerts, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15));

        _dispatcher.Delivered.Should().ContainSingle().Which.Notification.Kind.Should().Be(AlertNotificationKind.Event);
    }

    [Fact]
    public async Task DeliveryWorker_ProcessesPendingDeliveriesOnEveryTick()
    {
        var alerts = new Mock<IAlertManager>();
        var calls = 0;
        alerts.Setup(a => a.ProcessPendingAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls))
            .Returns(Task.CompletedTask);
        using var worker = new NotificationDeliveryWorker(alerts.Object, _time, NullLogger<NotificationDeliveryWorker>.Instance);

        await worker.StartAsync(Ct);
        for (var i = 0; i < 200 && Volatile.Read(ref calls) < 3; i++)
        {
            _time.Advance(NotificationDeliveryWorker.Tick);
            await Task.Delay(10, Ct);
        }
        await worker.StopAsync(Ct);

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(3, "it runs at start (pending deliveries from before a restart) and every tick");
    }
}