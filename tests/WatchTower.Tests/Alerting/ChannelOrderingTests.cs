using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;

using static WatchTower.Services.Alerting.AlertNotificationKind;

namespace WatchTower.Tests.Alerting;

/// <summary>
/// Each channel advances through an alert's queue on its own: it gets the notices in order (an
/// occurrence's recovery before the next occurrence's opening), a channel that fails never holds
/// back the ones that work, a failing channel is only attempted on its backoff schedule however
/// many notices wait for it, and a channel that comes back drains its backlog in order.
/// </summary>
public class ChannelOrderingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly RecordingDispatcher _recording = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static readonly AlertKey Key = AlertKey.Endpoint("site");

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private AlertManager Alerts(IAlertStateStore? state = null) =>
        TestAlerts.Create(_metrics.Object, _time, _recording, state: state);

    private Task Raise(AlertManager alerts, string title, AlertKey? key = null) =>
        alerts.RaiseAsync(key ?? Key, AlertSeverity.Critical, title, new Incident(Now, IncidentType.EndpointDown, title), Ct).Bounded();

    private Task<bool> Resolve(AlertManager alerts, AlertKey? key = null) => alerts.ResolveAsync(key ?? Key, ct: Ct).Bounded();

    private List<(AlertNotificationKind Kind, string Title)> Received(string channel, AlertKey? key = null) =>
        _recording.Delivered
            .Where(d => d.Channel == channel && (key is null || d.Notification.AlertKey == key.Value.Value))
            .Select(d => (d.Notification.Kind, d.Notification.Title))
            .ToList();

    private int AttemptsOn(string channel) => _recording.Attempts.Count(a => a.Channel == channel);

    private async Task RunFor(AlertManager alerts, TimeSpan duration, TimeSpan step)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step)
        {
            _time.Advance(step);
            await alerts.ProcessPendingAsync(Ct).Bounded();
        }
    }

    // ── The reviewer's probes ──────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRejectedTwiceByOneChannel_ThatChannelGetsItBeforeTheNextOpening(bool restart)
    {
        // Probe 1: ch2 rejects A's recovery twice; B opens; five minutes later ch2 accepts. ch2
        // used to receive B's opening between A's opening and A's recovery.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Channels = 2;
        var rejectRecovery = true;
        _recording.Rejects = (channel, n) => rejectRecovery && channel == "ch2" && n is { Kind: Resolved, Title: "A down" };
        var alerts = Alerts(restart ? new SqliteAlertStateStore(path) : null);

        await Raise(alerts, "A down");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Resolve(alerts);
        _time.Advance(TimeSpan.FromMinutes(1));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _recording.Attempts.Count(a => a is { Channel: "ch2", Notification.Kind: Resolved }).Should().Be(2);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Raise(alerts, "B down");
        Received("ch1").Should().Equal([(Opened, "A down"), (Resolved, "A down"), (Opened, "B down")],
            "the working channel is not held back");
        Received("ch2").Should().Equal([(Opened, "A down")], "B's opening waits behind A's recovery on ch2");
        _recording.Attempts.Count(a => a is { Channel: "ch2", Notification.Kind: Resolved }).Should().Be(2,
            "B's opening does not retry A's recovery before its deadline");

        if (restart)
        {
            var persisted = new SqliteAlertStateStore(path).LoadDeliveries();
            persisted.Select(d => (d.Notification.Kind, d.Notification.Title, d.DeliveredChannels.Single()))
                .Should().Equal((Resolved, "A down", "ch1"), (Opened, "B down", "ch1"));
            persisted[0].ChannelRetries.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Channel = "ch2", Attempts = 2 });
            persisted[1].ChannelRetries.Should().BeEmpty("B's opening was never attempted on ch2");
            alerts = Alerts(new SqliteAlertStateStore(path)); // a new process
        }

        rejectRecovery = false;
        _time.Advance(TimeSpan.FromMinutes(5));
        await alerts.ProcessPendingAsync(Ct).Bounded();

        foreach (var channel in new[] { "ch1", "ch2" })
            Received(channel).Should().Equal([(Opened, "A down"), (Resolved, "A down"), (Opened, "B down")], channel);
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Single().Should().BeEquivalentTo(new { Occurrence = 2L, NotifyCount = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlapStormAgainstADeadChannel_AttemptsItOnlyOnItsBackoffSchedule(bool restart)
    {
        // Probe 2: the channel is down; the alert opens and clears every second. Every new notice
        // used to retry the queue's head: 17,980 dispatches in one hour with 5 rows queued.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Failing.Add("ch1");
        var alerts = Alerts(restart ? new SqliteAlertStateStore(path) : null);
        // Every write is synced to disk: with the database, one transition every 10 seconds (still
        // far faster than any backoff, and the probe times below are multiples of it).
        var interval = restart ? 10 : 1;
        var hours = restart ? 1 : 2;
        var perHour = new List<int>();

        for (var second = 0; second < hours * 3600; second += interval)
        {
            if (restart && second == 1800)
                alerts = Alerts(new SqliteAlertStateStore(path)); // the circuit and the retries are persisted
            if (second / interval % 2 == 0)
                await Raise(alerts, "storm");
            else
                await Resolve(alerts);
            alerts.GetPendingDeliveries().Should().HaveCountLessThanOrEqualTo(AlertManager.MaxQueuedNotices);
            _time.Advance(TimeSpan.FromSeconds(interval));
            if ((second + interval) % 3600 == 0)
                perHour.Add(AttemptsOn("ch1") - perHour.Sum());
        }

        // Probes at 0, 1, 3, 7, 15 and 31 minutes, then every 30 minutes (61, 91 min).
        perHour[0].Should().Be(6).And.BeLessThanOrEqualTo(ChannelRateLimiter.WorstCaseFirstHourAttempts);
        if (!restart)
            perHour[1].Should().Be(2, "one probe every 30 minutes once the backoff is at its bound");
        _recording.Sent.Should().HaveCount(perHour.Sum(), "nothing is dispatched while no channel may be attempted");
        _recording.Delivered.Should().BeEmpty();
    }

    // ── A dead channel and the channels that work ──────────────────────────

    [Fact]
    public async Task ADeadChannel_NeverDelaysTheWorkingOne_AndGetsTheFlappingNoticeInItsPlace()
    {
        _recording.Channels = 2;
        _recording.Failing.Add("ch2");
        var alerts = Alerts();

        // Occurrence i opens at minute 2(i-1) and clears a minute later; ch1 has each notice at once.
        for (var i = 1; i <= 8; i++)
        {
            await Raise(alerts, $"down #{i}");
            Received("ch1").Last().Should().Be((Opened, $"down #{i}"));
            _time.Advance(TimeSpan.FromMinutes(1));
            await Resolve(alerts);
            Received("ch1").Last().Should().Be((Resolved, $"down #{i}"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Received("ch1").Should().HaveCount(16).And.NotContain(n => n.Title.StartsWith("Flapping"),
            "ch1 has every folded notice: the flapping notice is not sent to it");
        const string flapping = "Flapping: 4 occurrences between 2026-01-01 00:06:00 UTC and 2026-01-01 00:13:00 UTC";
        var queued = alerts.GetPendingDeliveries();
        queued.Select(d => d.Notification.Title).Should().Equal("down #1", "down #2", "down #3", flapping, "down #8");
        queued.Should().OnlyContain(d => d.DeliveredChannels.SequenceEqual(new[] { "ch1" }));
        AttemptsOn("ch2").Should().BeLessThanOrEqualTo(ChannelRateLimiter.WorstCaseFirstHourAttempts);

        // ch2 is back: its backlog drains in queue order, the flapping notice in the folded ones' place.
        _recording.Failing.Clear();
        await RunFor(alerts, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(1));

        Received("ch2").Should().Equal(
            (Resolved, "down #1"), (Resolved, "down #2"), (Resolved, "down #3"), (Resolved, flapping), (Resolved, "down #8"));
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task DeadChannelComingBack_DrainsEveryAlertInOrder_AfterOneProbe()
    {
        _recording.Channels = 2;
        _recording.Failing.Add("ch2");
        var alerts = Alerts();
        var keys = new[] { AlertKey.Endpoint("a"), AlertKey.Endpoint("b"), AlertKey.Endpoint("c") };
        foreach (var key in keys)
        {
            await Raise(alerts, $"{key.Value} first", key);
            await Resolve(alerts, key);
            await Raise(alerts, $"{key.Value} second", key);
        }
        foreach (var key in keys)
        {
            Received("ch1", key).Should().Equal(
                (Opened, $"{key.Value} first"), (Resolved, $"{key.Value} first"), (Opened, $"{key.Value} second"));
        }
        AttemptsOn("ch2").Should().Be(1, "the first failure opened ch2's circuit for every alert");

        // ch2 is back. Nothing is attempted before its circuit's probe is due; then everything drains.
        _recording.Failing.Clear();
        _time.Advance(TimeSpan.FromSeconds(30));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        AttemptsOn("ch2").Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(30));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        foreach (var key in keys)
            Received("ch2", key).Should().Equal((Resolved, $"{key.Value} first"), (Opened, $"{key.Value} second"));
        AttemptsOn("ch2").Should().Be(7, "the probe, then each remaining notice once");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ARecoveryGivenUpOnAChannel_LetsTheNextOpeningThroughThere()
    {
        // ch2 rejects A's recovery for good. Retries stop 24 hours after its first attempt; until
        // then B's opening waits behind it on ch2 (and only there).
        _recording.Channels = 2;
        _recording.Rejects = (channel, n) => channel == "ch2" && n is { Kind: Resolved, Title: "A down" };
        var alerts = Alerts();
        await Raise(alerts, "A down");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Resolve(alerts);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Raise(alerts, "B down");
        Received("ch1").Should().Equal((Opened, "A down"), (Resolved, "A down"), (Opened, "B down"));

        await RunFor(alerts, TimeSpan.FromHours(23), TimeSpan.FromMinutes(5));
        Received("ch2").Should().Equal([(Opened, "A down")], "B's opening still waits behind A's recovery");

        await RunFor(alerts, TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));
        Received("ch2").Should().Equal([(Opened, "A down"), (Opened, "B down")],
            "A's recovery was given up on ch2, so B's opening went through");
        var recoveryAttempts = _recording.Attempts.Count(a => a is { Channel: "ch2", Notification.Kind: Resolved });
        recoveryAttempts.Should().BeInRange(45, 60, "about every 30 minutes for 24 hours, then no more");
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(1);
    }

    // ── The rate limit of a channel that works ─────────────────────────────

    [Fact]
    public async Task ABurstOfAlerts_IsPacedPerChannel_OnceItsBurstIsUsedUp()
    {
        var alerts = Alerts();
        var keys = Enumerable.Range(1, ChannelRateLimiter.Burst + 5).Select(i => AlertKey.Endpoint($"site{i}")).ToList();
        foreach (var key in keys)
            await Raise(alerts, $"{key.Value} down", key);

        Received("ch1").Should().HaveCount(ChannelRateLimiter.Burst, "the rest waits for the channel's budget to refill");
        _recording.Sent.Should().HaveCount(ChannelRateLimiter.Burst);
        alerts.GetPendingDeliveries().Should().HaveCount(5).And.OnlyContain(d => d.FailedAttempts == 0);

        _time.Advance(ChannelRateLimiter.RefillInterval * 2);
        await alerts.ProcessPendingAsync(Ct).Bounded();
        Received("ch1").Should().HaveCount(ChannelRateLimiter.Burst + 2);

        _time.Advance(ChannelRateLimiter.RefillInterval * 3);
        await alerts.ProcessPendingAsync(Ct).Bounded();
        Received("ch1").Select(n => n.Title).Should().BeEquivalentTo(keys.Select(k => $"{k.Value} down"));
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Should().HaveCount(keys.Count).And.OnlyContain(s => s.NotifyCount == 1);
    }
}