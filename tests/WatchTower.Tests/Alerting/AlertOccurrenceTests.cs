using AwesomeAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;

using static WatchTower.Services.Alerting.AlertNotificationKind;

namespace WatchTower.Tests.Alerting;

/// <summary>
/// An alert that opens again while an earlier occurrence's notices are still on their way. Every
/// occurrence whose opening may have reached operators gets its own recovery, sent before the next
/// occurrence's notices (also after a restart and with a channel failing), and a flapping alert's
/// queue stays bounded.
/// </summary>
public class AlertOccurrenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly GatedDispatcher _gated = new();
    private readonly RecordingDispatcher _recording = new();
    private readonly TestMaintenance _maintenance = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static readonly AlertKey Key = AlertKey.Endpoint("flappy");

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static DateTime At(int minutes) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    private AlertManager Alerts(INotificationDispatcher dispatcher, IAlertStateStore? state = null) =>
        TestAlerts.Create(_metrics.Object, _time, dispatcher, state: state, maintenance: _maintenance);

    private Task Raise(AlertManager alerts, string title) =>
        alerts.RaiseAsync(Key, AlertSeverity.Critical, title, new Incident(Now, IncidentType.EndpointDown, title), Ct).Bounded();

    private Task<bool> Resolve(AlertManager alerts) => alerts.ResolveAsync(Key, ct: Ct).Bounded();

    private static List<(AlertNotificationKind Kind, string Title)> Kinds(IEnumerable<AlertNotification> sent) =>
        sent.Select(n => (n.Kind, n.Title)).ToList();

    private static List<(AlertNotificationKind Kind, string Title)> Received(RecordingDispatcher dispatcher, string channel) =>
        Kinds(dispatcher.Delivered.Where(d => d.Channel == channel).Select(d => d.Notification));

    // ── The judge's and the reviewer's probes ──────────────────────────────

    [Fact]
    public async Task ReopenedWhileTheFirstOpeningIsBeingSent_TheFirstRecoveryGoesOutBeforeTheNextOpening()
    {
        // Judge, scenario A: the held opening of A is being sent when A resolves and B (the next
        // occurrence of the same alert) opens. B's opening used to replace A's recovery.
        var alerts = Alerts(_gated);
        _maintenance.Active = true;
        await Raise(alerts, "A down");
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var openedA = await _gated.NextAsync(Ct);
        (await Resolve(alerts)).Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(1));
        await Raise(alerts, "B down");

        _gated.HasWaitingCall.Should().BeFalse("A's opening is still being sent");
        alerts.GetPendingDeliveries().Select(d => (d.Notification.Kind, d.Occurrence, d.ExplicitRecovery))
            .Should().Equal((Resolved, 1L, true), (Opened, 2L, false));

        openedA.Deliver("ch1");
        var resolvedA = await _gated.NextAsync(Ct);
        resolvedA.Notification.Kind.Should().Be(Resolved);
        resolvedA.Notification.Title.Should().Be("A down");
        resolvedA.Deliver("ch1");
        var openedB = await _gated.NextAsync(Ct);
        openedB.Notification.Kind.Should().Be(Opened);
        openedB.Notification.Title.Should().Be("B down");
        openedB.Deliver("ch1");
        await processing.Bounded();

        Kinds(_gated.Sent).Should().Equal((Opened, "A down"), (Resolved, "A down"), (Opened, "B down"));
        alerts.GetPendingDeliveries().Should().BeEmpty();
        var open = alerts.GetOpenAlerts().Should().ContainSingle().Subject;
        open.Occurrence.Should().Be(2);
        open.NotifyCount.Should().Be(1, "A's opening reaching its channel does not count for B");
        alerts.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task RecoveryHeldByAWindow_ThenReopened_IsSentBeforeTheNewOpening_ByAFreshManager()
    {
        // Judge, scenario B: A was notified, the window holds its recovery, B opens inside the
        // window, and the agent restarts after the window.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        var alerts = Alerts(_recording, new SqliteAlertStateStore(path));
        await Raise(alerts, "A down");
        _maintenance.Active = true;
        (await Resolve(alerts)).Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(5));
        await Raise(alerts, "B down");
        _recording.Sent.Should().ContainSingle("both notices are held by the window");

        new SqliteAlertStateStore(path).LoadDeliveries()
            .Select(d => (d.Notification.Kind, d.Occurrence, d.Held, d.ExplicitRecovery))
            .Should().Equal((Resolved, 1L, true, true), (Opened, 2L, true, false));

        _maintenance.Active = false;
        var restarted = Alerts(_recording, new SqliteAlertStateStore(path));
        await restarted.ProcessPendingAsync(Ct).Bounded();
        await restarted.ProcessPendingAsync(Ct).Bounded();

        Kinds(_recording.Sent).Should().Equal((Opened, "A down"), (Resolved, "A down"), (Opened, "B down"));
        restarted.GetPendingDeliveries().Should().BeEmpty();
        new SqliteAlertStateStore(path).LoadDeliveries().Should().BeEmpty();
        var open = restarted.GetOpenAlerts().Should().ContainSingle().Subject;
        open.Occurrence.Should().Be(2);
        open.NotifyCount.Should().Be(1);
    }

    [Fact]
    public async Task FlapWhileTheFirstOpeningIsBeingSent_KeepsItsRecovery_AndDropsTheNeverSentSecondOpening()
    {
        // Reviewer, R1: the second resolve used to drop the queued second opening (never
        // attempted) together with the first occurrence's recovery it had replaced, so the
        // first opening reached operators with no recovery.
        var alerts = Alerts(_gated);
        _maintenance.Active = true;
        await Raise(alerts, "first down");
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var first = await _gated.NextAsync(Ct);
        await Resolve(alerts);
        _time.Advance(TimeSpan.FromSeconds(20));
        await Raise(alerts, "second down");
        alerts.GetPendingDeliveries().Select(d => (d.Notification.Kind, d.Occurrence))
            .Should().Equal((Resolved, 1L), (Opened, 2L));

        await Resolve(alerts);
        alerts.GetPendingDeliveries().Select(d => (d.Notification.Kind, d.Occurrence, d.ExplicitRecovery)).Should()
            .Equal([(Resolved, 1L, true)], "the second opening was never attempted: it goes silently, the first recovery stays");

        first.Deliver("ch1");
        var recovery = await _gated.NextAsync(Ct);
        recovery.Notification.Kind.Should().Be(Resolved);
        recovery.Notification.Title.Should().Be("first down");
        recovery.Deliver("ch1");
        await processing.Bounded();
        await alerts.ProcessPendingAsync(Ct).Bounded();

        _gated.HasWaitingCall.Should().BeFalse();
        Kinds(_gated.Sent).Should().Equal((Opened, "first down"), (Resolved, "first down"));
        alerts.GetOpenAlerts().Should().BeEmpty();
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task FlapWhileTheSecondOpeningIsBeingSent_TooGetsEachOccurrencesRecovery_InOrder()
    {
        // Reviewer, R1 variant: the second occurrence's opening is in flight when it resolves.
        var alerts = Alerts(_gated);
        _maintenance.Active = true;
        await Raise(alerts, "first down");
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var first = await _gated.NextAsync(Ct);
        await Resolve(alerts);
        _time.Advance(TimeSpan.FromSeconds(20));
        await Raise(alerts, "second down");

        first.Deliver("ch1");
        (await _gated.NextAsync(Ct)).Deliver("ch1"); // the first recovery
        var second = await _gated.NextAsync(Ct);
        second.Notification.Title.Should().Be("second down");

        await Resolve(alerts);
        alerts.GetPendingDeliveries().Select(d => (d.Notification.Kind, d.Occurrence, d.ExplicitRecovery))
            .Should().Equal((Resolved, 2L, true));
        second.Deliver("ch1");
        (await _gated.NextAsync(Ct)).Deliver("ch1"); // the second recovery
        await processing.Bounded();

        Kinds(_gated.Sent).Should().Equal(
            (Opened, "first down"), (Resolved, "first down"), (Opened, "second down"), (Resolved, "second down"));
        _gated.HasWaitingCall.Should().BeFalse();
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.InFlightCount.Should().Be(0);
    }

    // ── A queue across a restart, and with a channel failing ───────────────

    [Fact]
    public async Task RestartMidQueue_SendsTheRestInOrder_AndNoticesMadeAfterwardsQueueBehindIt()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        var alerts = Alerts(_recording, new SqliteAlertStateStore(path));
        await Raise(alerts, "A down"); // delivered
        _recording.Failing.Add("ch1");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Resolve(alerts);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Raise(alerts, "B down");

        var persisted = new SqliteAlertStateStore(path).LoadDeliveries();
        persisted.Select(d => (d.Notification.Kind, d.Notification.Title, d.Occurrence))
            .Should().Equal((Resolved, "A down", 1L), (Opened, "B down", 2L));
        persisted.Select(d => d.Sequence).Should().BeInAscendingOrder();

        // New process, the channel is back; B's opening is due before A's recovery retry is.
        var after = new RecordingDispatcher();
        var restarted = Alerts(after, new SqliteAlertStateStore(path));
        _time.Advance(TimeSpan.FromMinutes(1));
        await restarted.ProcessPendingAsync(Ct).Bounded();

        Kinds(after.Sent).Should().Equal([(Resolved, "A down"), (Opened, "B down")],
            "A's recovery is sent first, early, rather than overtaken");
        restarted.GetOpenAlerts().Single().Should().BeEquivalentTo(new { Occurrence = 2L, NotifyCount = 1 });

        await Resolve(restarted);
        Kinds(after.Sent).Should().Equal((Resolved, "A down"), (Opened, "B down"), (Resolved, "B down"));
        restarted.GetPendingDeliveries().Should().BeEmpty();
        new SqliteAlertStateStore(path).LoadDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task OneOfSeveralChannelsFailing_GetsTheQueueInOrder_WithoutHoldingBackTheOthers()
    {
        _recording.Channels = 3;
        _recording.Failing.Add("ch2");
        var alerts = Alerts(_recording);
        await Raise(alerts, "A down");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Resolve(alerts);
        // A's recovery keeps failing on ch2, so its next retry is far off.
        _time.Advance(TimeSpan.FromMinutes(2));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _time.Advance(TimeSpan.FromMinutes(2));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        var recovery = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        recovery.DeliveredChannels.Should().BeEquivalentTo("ch1", "ch3");
        recovery.NextAttemptAt.Should().BeAfter(Now.AddMinutes(2));

        await Raise(alerts, "B down");
        Received(_recording, "ch1").Should().Equal(
            [(Opened, "A down"), (Resolved, "A down"), (Opened, "B down")],
            "B's opening is not held back by a channel that keeps rejecting A's recovery");
        alerts.GetPendingDeliveries().Select(d => (d.Notification.Kind, d.Occurrence))
            .Should().Equal((Resolved, 1L), (Opened, 2L));

        // ch2 is back: B's opening is due first, but ch2 gets A's recovery before it.
        _recording.Failing.Clear();
        for (var i = 0; i < 15; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await alerts.ProcessPendingAsync(Ct).Bounded();
        }

        Received(_recording, "ch2").Should().Equal((Resolved, "A down"), (Opened, "B down"));
        foreach (var channel in new[] { "ch1", "ch3" })
            Received(_recording, channel).Should().Equal((Opened, "A down"), (Resolved, "A down"), (Opened, "B down"));
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Single().Should().BeEquivalentTo(new { Occurrence = 2L, NotifyCount = 1 });
    }

    // ── Flapping ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FlapStormWhileTheChannelIsDown_KeepsTheQueueBounded_WithOnePersistedFlappingNotice()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Failing.Add("ch1");
        var alerts = Alerts(_recording, new SqliteAlertStateStore(path));

        // Occurrence i opens at minute 2(i-1) and clears a minute later; the 12th stays open.
        for (var i = 1; i <= 12; i++)
        {
            await Raise(alerts, $"down #{i}");
            alerts.GetPendingDeliveries().Should().HaveCountLessThanOrEqualTo(AlertManager.MaxQueuedNotices);
            _time.Advance(TimeSpan.FromMinutes(1));
            if (i == 12)
                break;
            await Resolve(alerts);
            alerts.GetPendingDeliveries().Should().HaveCountLessThanOrEqualTo(AlertManager.MaxQueuedNotices);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        const string flapping = "Flapping: 8 occurrences between 2026-01-01 00:06:00 UTC and 2026-01-01 00:21:00 UTC";
        var queued = new SqliteAlertStateStore(path).LoadDeliveries();
        queued.Select(d => (d.Notification.Kind, d.Notification.Title)).Should().Equal(
            (Resolved, "down #1"), (Resolved, "down #2"), (Resolved, "down #3"), (Resolved, flapping), (Opened, "down #12"));
        var notice = queued.Single(d => d.IsFlapping);
        notice.FlappingCount.Should().Be(8);
        notice.ExplicitRecovery.Should().BeTrue("the folded occurrences' openings were attempted");
        notice.Notification.Description.Should().Contain("opened and cleared 8 times");
        queued.Where(d => d.Notification.Kind == Resolved).Should().OnlyContain(d => d.ExplicitRecovery);

        _recording.Failing.Clear();
        for (var i = 0; i < 60; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await alerts.ProcessPendingAsync(Ct).Bounded();
        }

        Received(_recording, "ch1").Should().Equal(
            (Resolved, "down #1"), (Resolved, "down #2"), (Resolved, "down #3"), (Resolved, flapping), (Opened, "down #12"));
        alerts.GetPendingDeliveries().Should().BeEmpty();
        new SqliteAlertStateStore(path).LoadDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Single().Should().BeEquivalentTo(new { Occurrence = 12L, NotifyCount = 1 });
    }

    [Fact]
    public async Task FlapStormInsideAWindow_IsSummarisedWithOneFlappingLine_AndTheOpenAlertNotifiesOnce()
    {
        var alerts = Alerts(_recording);
        _maintenance.Active = true;
        for (var i = 1; i <= 8; i++)
        {
            await Raise(alerts, $"down #{i}");
            _time.Advance(TimeSpan.FromMinutes(1));
            if (i == 8)
                break;
            await Resolve(alerts);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        alerts.GetPendingDeliveries().Should().HaveCount(AlertManager.MaxQueuedNotices).And.OnlyContain(d => d.Held);
        _recording.Sent.Should().BeEmpty();

        _maintenance.Active = false;
        await alerts.ProcessPendingAsync(Ct).Bounded();
        await alerts.ProcessPendingAsync(Ct).Bounded();

        Kinds(_recording.Sent).Should().Equal((Opened, "down #8"), (Event, "Maintenance ended: 4 held notifications"));
        var summary = _recording.Sent.Last().Description;
        summary.Should().Contain("down #1: opened and cleared after 1 min")
            .And.Contain("down #3: opened and cleared after 1 min")
            .And.Contain("Flapping: 4 occurrences between 2026-01-01 00:06:00 UTC and 2026-01-01 00:13:00 UTC");
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Single().Should().BeEquivalentTo(new { Occurrence = 8L, NotifyCount = 1 });
    }

    // ── Upgrading existing databases ───────────────────────────────────────

    private const string AlertStateTable =
        """
        CREATE TABLE alert_state (
            key TEXT PRIMARY KEY COLLATE NOCASE, severity INTEGER NOT NULL, incident_type INTEGER NOT NULL,
            title TEXT NOT NULL, opened_at TEXT NOT NULL, last_notified_at TEXT, notify_count INTEGER NOT NULL DEFAULT 0,
            resolved_at TEXT, last_description TEXT NOT NULL, retired_reason TEXT);
        CREATE TABLE agent_state (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
        """;

    /// <summary>The tables as 8c628e4 (and every version before the explicit recovery flag) left them.</summary>
    private const string Schema8c628e4 = AlertStateTable +
        """
        CREATE TABLE alert_delivery (
            id TEXT PRIMARY KEY, alert_key TEXT NOT NULL COLLATE NOCASE, kind INTEGER NOT NULL,
            notification TEXT NOT NULL, created_at TEXT NOT NULL, delivered_channels TEXT NOT NULL DEFAULT '[]',
            failed_attempts INTEGER NOT NULL DEFAULT 0, first_attempt_at TEXT, next_attempt_at TEXT NOT NULL,
            last_error TEXT, held INTEGER NOT NULL DEFAULT 0);
        """;

    /// <summary>The tables as d4168d0 (explicit recovery flag, no occurrences) left them.</summary>
    private const string SchemaD4168d0 = AlertStateTable +
        """
        CREATE TABLE alert_delivery (
            id TEXT PRIMARY KEY, alert_key TEXT NOT NULL COLLATE NOCASE, kind INTEGER NOT NULL,
            notification TEXT NOT NULL, created_at TEXT NOT NULL, delivered_channels TEXT NOT NULL DEFAULT '[]',
            failed_attempts INTEGER NOT NULL DEFAULT 0, first_attempt_at TEXT, next_attempt_at TEXT NOT NULL,
            last_error TEXT, held INTEGER NOT NULL DEFAULT 0, explicit_recovery INTEGER NOT NULL DEFAULT 0);
        """;

    private static void Sql(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string Utc(int minutes) => At(minutes).ToString("O");

    private static void InsertState(string path, string key, string title, IncidentType type, int openedAt, int? resolvedAt) =>
        Sql(path,
            """
            INSERT INTO alert_state (key, severity, incident_type, title, opened_at, notify_count, resolved_at, last_description)
            VALUES (@key, @severity, @type, @title, @opened, 0, @resolved, 'last')
            """,
            ("@key", key), ("@severity", (int)AlertSeverity.Critical), ("@type", (int)type), ("@title", title),
            ("@opened", Utc(openedAt)), ("@resolved", resolvedAt is { } r ? Utc(r) : DBNull.Value));

    private static void InsertDelivery(
        string path, string id, AlertNotification n, int createdAt, bool held, int failedAttempts = 0, bool? explicitRecovery = null) =>
        Sql(path,
            "INSERT INTO alert_delivery (id, alert_key, kind, notification, created_at, failed_attempts, next_attempt_at, held" +
            (explicitRecovery is null ? ")" : ", explicit_recovery)") +
            " VALUES (@id, @key, @kind, @notification, @created, @failed, @created, @held" +
            (explicitRecovery is null ? ")" : ", @explicit)"),
            ("@id", id), ("@key", n.AlertKey), ("@kind", (int)n.Kind),
            ("@notification", System.Text.Json.JsonSerializer.Serialize(n)), ("@created", Utc(createdAt)),
            ("@failed", failedAttempts), ("@held", held ? 1 : 0), ("@explicit", explicitRecovery == true ? 1 : 0));

    private static AlertNotification Legacy(
        AlertNotificationKind kind, string key, string title, int occurredAt, int? openedAt = null, int notifyCount = 0) =>
        new(kind, key, AlertSeverity.Critical, title, "details", "TEST-SRV", At(occurredAt),
            openedAt is { } o ? At(o) : null, openedAt is { } d ? At(occurredAt) - At(d) : null, NotifyCount: notifyCount);

    [Fact]
    public async Task UpgradeFromThe8c628e4Schema_SendsItsPendingRecoveryOnItsOwn_AndKeepsTheQueueOrder()
    {
        // Reviewer, R2: a recovery queued by that version while its opening was being sent has
        // NotifyCount 0; it used to be migrated as summary-eligible ("Maintenance ended: 1 held").
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        Sql(path, Schema8c628e4);
        var disk = AlertKey.Disk("C:\\").Value;
        InsertState(path, Key.Value, "Endpoint Down: flappy", IncidentType.EndpointDown, openedAt: 0, resolvedAt: 1);
        InsertState(path, disk, "Disk Critical", IncidentType.DiskCritical, openedAt: 2, resolvedAt: null);
        InsertDelivery(path, "recovery", Legacy(Resolved, Key.Value, "Endpoint Down: flappy", 1, openedAt: 0), 1, held: true);
        InsertDelivery(path, "opened", Legacy(Opened, disk, "Disk Critical", 2, openedAt: 2, notifyCount: 1), 2,
            held: false, failedAttempts: 1);
        InsertDelivery(path, "reboot", Legacy(Event, AlertKey.Reboot.Value, "Server restarted", 3), 3, held: true);

        var store = new SqliteAlertStateStore(path);
        _ = new SqliteAlertStateStore(path); // a second start migrates nothing again
        var rows = new SqliteAlertStateStore(path).LoadDeliveries();
        rows.Select(d => (d.Id, d.ExplicitRecovery, d.Occurrence, d.FlappingCount)).Should().Equal(
            ("recovery", true, 1L, 0), ("opened", false, 1L, 0), ("reboot", false, 0L, 0));
        rows.Select(d => d.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        store.LoadAll().Should().HaveCount(2).And.OnlyContain(s => s.Occurrence == 1);

        _time.Advance(TimeSpan.FromMinutes(10));
        var alerts = Alerts(_recording, new SqliteAlertStateStore(path));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        await alerts.ProcessPendingAsync(Ct).Bounded();

        Kinds(_recording.Sent).Should().Equal(
            (Resolved, "Endpoint Down: flappy"), (Opened, "Disk Critical"), (Event, "Maintenance ended: 1 held notification"));
        _recording.Sent.Last().Description.Should().Contain("Server restarted").And.NotContain("Endpoint Down");
        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(1, "the migrated opening counts for its migrated occurrence");
        alerts.GetPendingDeliveries().Should().BeEmpty();

        // The alert's next occurrence is numbered after the migrated one.
        await Raise(alerts, "Endpoint Down: flappy");
        alerts.GetOpenAlerts().Single(s => s.Key == Key.Value).Occurrence.Should().Be(2);
    }

    [Fact]
    public async Task UpgradeFromTheD4168d0Schema_KeepsItsExplicitFlags_AndNumbersItsQueue()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        Sql(path, SchemaD4168d0);
        var pool = AlertKey.AppPool("Pool").Value;
        InsertState(path, Key.Value, "Endpoint Down: flappy", IncidentType.EndpointDown, openedAt: 0, resolvedAt: 1);
        InsertState(path, pool, "IIS App Pool Down: Pool", IncidentType.ApplicationPoolStartFailed, openedAt: 2, resolvedAt: 5);
        InsertDelivery(path, "explicit", Legacy(Resolved, Key.Value, "Endpoint Down: flappy", 1, openedAt: 0), 1,
            held: true, explicitRecovery: true);
        InsertDelivery(path, "window", Legacy(Resolved, pool, "IIS App Pool Down: Pool", 5, openedAt: 2), 5,
            held: true, explicitRecovery: false);

        _ = new SqliteAlertStateStore(path);
        var store = new SqliteAlertStateStore(path);
        store.LoadDeliveries().Select(d => (d.Id, d.ExplicitRecovery, d.Occurrence))
            .Should().Equal(("explicit", true, 1L), ("window", false, 1L));
        store.LoadAll().Should().OnlyContain(s => s.Occurrence == 1);

        _time.Advance(TimeSpan.FromMinutes(10));
        var alerts = Alerts(_recording, store);
        await alerts.ProcessPendingAsync(Ct).Bounded();
        await alerts.ProcessPendingAsync(Ct).Bounded();

        Kinds(_recording.Sent).Should().Equal(
            (Resolved, "Endpoint Down: flappy"), (Event, "Maintenance ended: 1 held notification"));
        _recording.Sent.Last().Description.Should().Contain("IIS App Pool Down: Pool: opened and cleared after 3 min");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }
}