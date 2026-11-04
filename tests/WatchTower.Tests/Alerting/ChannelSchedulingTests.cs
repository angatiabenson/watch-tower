using AwesomeAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;

using static WatchTower.Services.Alerting.AlertNotificationKind;

namespace WatchTower.Tests.Alerting;

/// <summary>
/// Channel obligations fixed when a notice is created, and the one fair scheduler every send goes
/// through: a configuration change never brings an old obligation back, fresh traffic never
/// starves queued work, a critical notice goes first and a lower one still goes out (aging).
/// </summary>
public class ChannelSchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly RecordingDispatcher _recording = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static readonly AlertKey Key = AlertKey.Endpoint("site");

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private AlertManager Alerts(IAlertStateStore? state = null, INotificationDispatcher? dispatcher = null) =>
        TestAlerts.Create(_metrics.Object, _time, dispatcher ?? _recording, state: state);

    private Task Raise(AlertManager alerts, string title, AlertSeverity severity = AlertSeverity.Critical, AlertKey? key = null) =>
        alerts.RaiseAsync(key ?? Key, severity, title, new Incident(Now, IncidentType.EndpointDown, title), Ct).Bounded();

    private Task<bool> Resolve(AlertManager alerts, AlertKey? key = null) => alerts.ResolveAsync(key ?? Key, ct: Ct).Bounded();

    private List<(AlertNotificationKind Kind, string Title)> Received(string channel) =>
        _recording.Delivered.Where(d => d.Channel == channel).Select(d => (d.Notification.Kind, d.Notification.Title)).ToList();

    private int DeliveredTitled(string title) => _recording.Delivered.Count(d => d.Notification.Title == title);

    // ── A: obligations fixed at enqueue ────────────────────────────────────

    /// <summary>A webhook sender: the channels in <see cref="RejectAOn"/> reject A's recovery.</summary>
    private sealed class FilterSender : INotificationChannel
    {
        public HashSet<string> RejectAOn { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<(AlertNotificationKind Kind, string Title)>> Received { get; } = new()
        {
            ["good"] = [],
            ["intermittent"] = [],
        };

        public NotificationChannelType Type => NotificationChannelType.Webhook;

        public bool IsAvailable(NotificationChannel channel) => true;

        public Task<ChannelSendResult> SendAsync(NotificationChannel channel, AlertNotification n, CancellationToken ct)
        {
            var ok = !(RejectAOn.Contains(channel.Name) && n is { Kind: Resolved, Title: "A" });
            if (ok)
                Received[channel.Name].Add((n.Kind, n.Title));
            return Task.FromResult(ok ? ChannelSendResult.Ok() : ChannelSendResult.Failed("Rejected with HTTP 503"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeverityFilterRaisedAndRestored_NeverResurrectsAnOldObligation(bool restart)
    {
        // Reviewer's probe 1: A (a warning) clears and its recovery fails on "intermittent"; its
        // MinSeverity is raised to Critical, so B (critical) passes; the filter is restored. The
        // recovery used to be sent then, after B's opening: Opened A, Opened B, Resolved A.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        var options = new NotificationOptions
        {
            Email = new EmailNotificationSettings { Enabled = false },
            Channels =
            [
                new NotificationChannelOptions { Name = "good", Type = "Webhook", Url = "https://example.org/good" },
                new NotificationChannelOptions { Name = "intermittent", Type = "Webhook", Url = "https://example.org/intermittent" },
            ],
        };
        var sender = new FilterSender();
        sender.RejectAOn.UnionWith(["good", "intermittent"]);
        var dispatcher = new NotificationDispatcher([sender], new TestOptionsMonitor<NotificationOptions>(options),
            NullLogger<NotificationDispatcher>.Instance);
        var alerts = Alerts(new SqliteAlertStateStore(path), dispatcher);

        await Raise(alerts, "A", AlertSeverity.Warning);
        await Resolve(alerts);
        sender.Received["intermittent"].Should().Equal((Opened, "A"));

        options.Channels[1].MinSeverity = "Critical";
        sender.RejectAOn.Remove("intermittent"); // it would accept A's recovery now
        _time.Advance(TimeSpan.FromMinutes(1));
        await Raise(alerts, "B");
        sender.Received["intermittent"].Should().Equal([(Opened, "A"), (Opened, "B")],
            "A's recovery is no longer owed to intermittent (its filter excludes it now), so B passes");
        sender.Received["good"].Should().Equal([(Opened, "A")], "good still owes A's recovery first");

        var recorded = new SqliteAlertStateStore(path).LoadDeliveries();
        recorded.Select(d => (d.Notification.Kind, d.Notification.Title)).Should().Equal((Resolved, "A"), (Opened, "B"));
        recorded[0].SkippedChannels.Should().Equal(["intermittent"], "the skip is recorded");
        recorded[0].Channels.Should().Equal("good", "intermittent");

        if (restart)
            alerts = Alerts(new SqliteAlertStateStore(path), dispatcher);
        options.Channels[1].MinSeverity = "Info";
        sender.RejectAOn.Clear();
        _time.Advance(TimeSpan.FromHours(1));
        await alerts.ProcessPendingAsync(Ct).Bounded();

        sender.Received["intermittent"].Should().Equal([(Opened, "A"), (Opened, "B")], "restoring the filter brings nothing back");
        sender.Received["good"].Should().Equal((Opened, "A"), (Resolved, "A"), (Opened, "B"));
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelRemovedWhileNoticesAreQueued_IsSkippedForThem_AndReAddingItBringsNothingBack(bool restart)
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Channels = 2;
        _recording.Failing.UnionWith(["ch1", "ch2"]);
        var alerts = Alerts(restart ? new SqliteAlertStateStore(path) : null);

        await Raise(alerts, "A down", AlertSeverity.Warning);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Resolve(alerts);
        alerts.GetPendingDeliveries().Should().ContainSingle().Which.Channels.Should().Equal("ch1", "ch2");

        _recording.Channels = 1; // ch2 removed
        await alerts.ProcessPendingAsync(Ct).Bounded();
        var recovery = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        recovery.SkippedChannels.Should().Equal("ch2");
        recovery.ChannelRetries.Should().OnlyContain(r => r.Channel == "ch1");

        if (restart)
        {
            new SqliteAlertStateStore(path).LoadDeliveries().Single().SkippedChannels.Should().Equal("ch2");
            alerts = Alerts(new SqliteAlertStateStore(path));
        }

        _recording.Channels = 2; // ch2 added back
        _recording.Failing.Clear();
        _time.Advance(TimeSpan.FromMinutes(30));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        await Raise(alerts, "B down", AlertSeverity.Warning);

        Received("ch1").Should().Equal((Resolved, "A down"), (Opened, "B down"));
        Received("ch2").Should().Equal([(Opened, "B down")], "only notices created after it was added go to it");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ChannelAddedWhileNoticesAreQueued_OnlyGetsTheNoticesCreatedAfterwards()
    {
        _recording.Failing.Add("ch1");
        var alerts = Alerts();
        await Raise(alerts, "a down", AlertSeverity.Warning, AlertKey.Endpoint("a"));

        _recording.Channels = 2; // ch2 added
        await Raise(alerts, "b down", AlertSeverity.Warning, AlertKey.Endpoint("b"));
        Received("ch2").Should().Equal([(Opened, "b down")], "ch2 works, and b's opening was created after it was added");

        _recording.Failing.Clear();
        _time.Advance(TimeSpan.FromMinutes(5));
        await alerts.ProcessPendingAsync(Ct).Bounded();

        Received("ch1").Should().Equal((Opened, "a down"), (Opened, "b down"));
        Received("ch2").Should().Equal([(Opened, "b down")], "a's opening was never owed to ch2");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    // ── B: one fair scheduler ──────────────────────────────────────────────

    private async Task UseUpTheBurst(AlertManager alerts, int alreadyUsed = 0)
    {
        for (var i = 0; i < ChannelRateLimiter.Burst - alreadyUsed; i++)
            await Raise(alerts, "warm", AlertSeverity.Info, AlertKey.Endpoint($"warm{i}"));
        _recording.Delivered.Should().HaveCount(ChannelRateLimiter.Burst);
    }

    [Fact]
    public async Task FreshTrafficNeverStarvesAQueuedCriticalAlert()
    {
        // Reviewer's probe 2: the burst is used up, a critical opening waits for a token, and a
        // fresh info opening arrives every 2 seconds, just before each delivery-worker pass. All
        // 1,800 fresh notices used to go out in that hour, and the critical one never did.
        var alerts = Alerts();
        await UseUpTheBurst(alerts);
        await Raise(alerts, "old-critical", key: AlertKey.Endpoint("old-critical"));
        DeliveredTitled("old-critical").Should().Be(0, "no token is left");

        for (var i = 0; i < 1800; i++)
        {
            _time.Advance(ChannelRateLimiter.RefillInterval);
            await Raise(alerts, "fresh", AlertSeverity.Info, AlertKey.Endpoint($"fresh{i}"));
            if (i == 0)
                DeliveredTitled("old-critical").Should().Be(1, "the first token goes to the critical alert, not the fresh raise");
            await alerts.ProcessPendingAsync(Ct).Bounded();
        }

        DeliveredTitled("old-critical").Should().Be(1);
        DeliveredTitled("fresh").Should().BeInRange(1798, 1799, "one token every 2 seconds, the first went to the critical");
        _recording.Delivered.Select(d => d.Notification.Title).Where(t => t != "warm").First().Should().Be("old-critical");
    }

    [Fact]
    public async Task UnderLoad_ACriticalAlertGoesOutWithinOneSchedulingCycleOnceATokenIsAvailable()
    {
        var alerts = Alerts();
        await UseUpTheBurst(alerts);
        for (var i = 0; i < 5; i++)
            await Raise(alerts, "queued-info", AlertSeverity.Info, AlertKey.Endpoint($"info{i}"));
        await Raise(alerts, "queued-warning", AlertSeverity.Warning, AlertKey.Endpoint("warning"));
        await Raise(alerts, "critical", key: AlertKey.Endpoint("critical"));
        _recording.Delivered.Should().HaveCount(ChannelRateLimiter.Burst, "nothing more until a token is back");

        _time.Advance(ChannelRateLimiter.RefillInterval);
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _recording.Delivered.Skip(ChannelRateLimiter.Burst).Select(d => d.Notification.Title)
            .Should().Equal(["critical"], "it outranks the older warning and info notices");

        _time.Advance(ChannelRateLimiter.RefillInterval);
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _time.Advance(ChannelRateLimiter.RefillInterval);
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _recording.Delivered.Skip(ChannelRateLimiter.Burst).Select(d => d.Notification.AlertKey)
            .Should().Equal([AlertKey.Endpoint("critical").Value, AlertKey.Endpoint("warning").Value, AlertKey.Endpoint("info0").Value],
                "then by severity, then the oldest first");
    }

    [Fact]
    public async Task ARaise_NeverTakesATokenAheadOfOlderWorkOfTheSamePriority()
    {
        var alerts = Alerts();
        await UseUpTheBurst(alerts);
        await Raise(alerts, "older", AlertSeverity.Info, AlertKey.Endpoint("older"));

        _time.Advance(ChannelRateLimiter.RefillInterval);
        await Raise(alerts, "newer", AlertSeverity.Info, AlertKey.Endpoint("newer"));

        DeliveredTitled("older").Should().Be(1, "the raise's scheduling pass sent the older notice with the one token");
        DeliveredTitled("newer").Should().Be(0);
        alerts.GetPendingDeliveries().Should().ContainSingle().Which.Notification.Title.Should().Be("newer");
    }

    [Fact]
    public async Task UnderConstantCriticalLoad_AnInfoNoticeStillGoesOut_ByAging()
    {
        var alerts = Alerts();
        await UseUpTheBurst(alerts);
        await Raise(alerts, "patient", AlertSeverity.Info, AlertKey.Endpoint("patient"));

        // One new critical alert per token, for 15 minutes.
        TimeSpan? deliveredAfter = null;
        var waited = TimeSpan.Zero;
        for (var i = 0; waited < TimeSpan.FromMinutes(15); i++)
        {
            _time.Advance(ChannelRateLimiter.RefillInterval);
            waited += ChannelRateLimiter.RefillInterval;
            await Raise(alerts, "load", key: AlertKey.Endpoint($"load{i}"));
            await alerts.ProcessPendingAsync(Ct).Bounded();
            if (deliveredAfter is null && DeliveredTitled("patient") > 0)
                deliveredAfter = waited;
        }

        deliveredAfter.Should().NotBeNull("aging lifts it to critical, and then it is the oldest");
        deliveredAfter!.Value.Should().BeGreaterThanOrEqualTo(AlertManager.AgingInterval * 2,
            "critical notices go first until it has aged to their priority");
        deliveredAfter.Value.Should().BeLessThanOrEqualTo(AlertManager.AgingInterval * 2 + ChannelRateLimiter.RefillInterval);
        DeliveredTitled("patient").Should().Be(1);
    }

    [Fact]
    public async Task ACriticalOpeningBehindAnAlertsWarningRecovery_LendsItsPriorityToIt()
    {
        // Ordering per channel holds the critical opening behind the warning recovery of the same
        // alert; the recovery competes with the opening's priority, so the opening is not delayed by
        // older unrelated warnings.
        var alerts = Alerts();
        await Raise(alerts, "A", AlertSeverity.Warning);
        await UseUpTheBurst(alerts, alreadyUsed: 1);
        await Raise(alerts, "older warning", AlertSeverity.Warning, AlertKey.Endpoint("noise"));
        await Resolve(alerts); // A's recovery (a warning) waits for a token
        await Raise(alerts, "B"); // B (critical) waits behind A's recovery

        for (var i = 0; i < 3; i++)
        {
            _time.Advance(ChannelRateLimiter.RefillInterval);
            await alerts.ProcessPendingAsync(Ct).Bounded();
        }

        _recording.Delivered.Skip(ChannelRateLimiter.Burst).Select(d => (d.Notification.Kind, d.Notification.Title))
            .Should().Equal((Resolved, "A"), (Opened, "B"), (Opened, "older warning"));
    }

    // ── E: migration from the 9ebbde4 schema ───────────────────────────────

    [Fact]
    public async Task UpgradeFromThe9ebbde4Schema_DerivesObligationsOnce_KeepsDeliveredChannels_AndClampsRetries()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;

        // The database as 9ebbde4 left it: the current schema without the obligation columns.
        _ = new SqliteAlertStateStore(path);
        Sql(path, "ALTER TABLE alert_delivery DROP COLUMN channels; ALTER TABLE alert_delivery DROP COLUMN skipped_channels;");
        Sql(path,
            """
            INSERT INTO alert_state (key, severity, incident_type, title, opened_at, last_notified_at, notify_count,
                                     resolved_at, last_description, occurrence)
            VALUES (@key, 2, @type, 'B down', @at, @at, 1, NULL, 'last', 2)
            """,
            ("@key", Key.Value), ("@type", (int)IncidentType.EndpointDown), ("@at", Utc(Now)));
        void Row(string id, AlertNotificationKind kind, string title, long occurrence, string retries) =>
            Sql(path,
                """
                INSERT INTO alert_delivery (id, alert_key, kind, notification, created_at, delivered_channels, failed_attempts,
                                            first_attempt_at, next_attempt_at, last_error, held, explicit_recovery, occurrence,
                                            seq, flap_count, channel_retries)
                VALUES (@id, @key, @kind, @notification, @at, '["ch1"]', 3, @at, @next, 'ch2: 503', 0, @explicit,
                        @occurrence, @occurrence, 0, @retries)
                """,
                ("@id", id), ("@key", Key.Value), ("@kind", (int)kind),
                ("@notification", System.Text.Json.JsonSerializer.Serialize(new AlertNotification(
                    kind, Key.Value, AlertSeverity.Critical, title, "details", "TEST-SRV", Now, Now, TimeSpan.Zero, NotifyCount: 1))),
                ("@at", Utc(Now)), ("@next", Utc(Now.AddHours(2))), ("@explicit", kind == Resolved ? 1 : 0),
                ("@occurrence", occurrence), ("@retries", retries));
        Row("recovery", Resolved, "A down", 1,
            $$"""[{"Channel":"ch2","Attempts":3,"NextAttemptAt":"{{Utc(Now.AddHours(2))}}","LastError":"503"}]""");
        Row("opened", Opened, "B down", 2, "[]");

        _ = new SqliteAlertStateStore(path);
        var store = new SqliteAlertStateStore(path); // a second start migrates nothing again
        store.LoadDeliveries().Should().HaveCount(2).And.OnlyContain(d => d.Channels == null && d.SkippedChannels.Count == 0,
            "nothing was recorded before; the alert manager derives them");

        _recording.Channels = 2;
        var alerts = Alerts(store);
        var pending = alerts.GetPendingDeliveries();
        pending.Should().OnlyContain(d => d.Channels!.SequenceEqual(new[] { "ch1", "ch2" }) && d.DeliveredChannels.SequenceEqual(new[] { "ch1" }),
            "derived from the configuration at migration time, ch1 still delivered");
        pending[0].ChannelRetries.Single().NextAttemptAt.Should().Be(Now + NotificationRetryPolicy.Backoff(3),
            "a persisted deadline is clamped to now + its backoff");
        new SqliteAlertStateStore(path).LoadDeliveries().Should().OnlyContain(d => d.Channels!.Count == 2, "written back once");

        _recording.Channels = 3; // added after the migration: owed nothing queued before it
        _time.Advance(NotificationRetryPolicy.Backoff(3));
        await alerts.ProcessPendingAsync(Ct).Bounded();

        Received("ch2").Should().Equal((Resolved, "A down"), (Opened, "B down"));
        Received("ch1").Should().BeEmpty();
        Received("ch3").Should().BeEmpty();
        alerts.GetPendingDeliveries().Should().BeEmpty();
        new SqliteAlertStateStore(path).LoadDeliveries().Should().BeEmpty();
    }

    private static string Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

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
}