using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;

namespace WatchTower.Tests.Alerting;

/// <summary>
/// A channel backing off after failures is probed early by a new critical alert (at most once per
/// cooldown), and every backoff is measured on the monotonic clock: a wall-clock change neither
/// extends a wait (also across a restart, where persisted deadlines are clamped) nor starts a storm.
/// </summary>
public class CriticalProbeAndClockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly RecordingDispatcher _recording = new();
    private readonly SkewedTimeProvider _clock = new(new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

    private AlertManager Alerts(IAlertStateStore? state = null, NotificationOptions? notifications = null) =>
        TestAlerts.Create(_metrics.Object, _clock, _recording, state: state, notifications: notifications);

    private Task Raise(AlertManager alerts, string name, AlertSeverity severity) =>
        alerts.RaiseAsync(AlertKey.Endpoint(name), severity, name,
            new Incident(_clock.UtcNow, IncidentType.EndpointDown, name), Ct).Bounded();

    private int DeliveredTitled(string title) => _recording.Delivered.Count(d => d.Notification.Title == title);

    private async Task Pass(AlertManager alerts, TimeSpan advance)
    {
        _clock.Advance(advance);
        await alerts.ProcessPendingAsync(Ct).Bounded();
    }

    // ── C: the critical early probe ────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredChannel_DeliversANewCriticalAlertAtOnce_NotAfterItsBackoff(bool restart)
    {
        // Reviewer's probe 3: a warning keeps failing until the channel's backoff has matured
        // (30 minutes); the channel comes back and an unrelated critical alert opens. It used to
        // wait for the next probe: nothing at +29 minutes, delivered at +30.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Failing.Add("ch1");
        var alerts = Alerts(new SqliteAlertStateStore(path));
        await Raise(alerts, "warning", AlertSeverity.Warning);
        foreach (var minutes in new[] { 1, 2, 4, 8, 16 })
            await Pass(alerts, TimeSpan.FromMinutes(minutes));
        _recording.Attempts.Should().HaveCount(6, "the warning, on the channel's backoff schedule");

        if (restart)
            alerts = Alerts(new SqliteAlertStateStore(path)); // the circuit (30 minutes to go) is persisted
        _recording.Failing.Clear();
        await Raise(alerts, "critical-new", AlertSeverity.Critical);

        // The probe carries the channel's best notice: the warning, aged to critical over its 31
        // minutes and older. It succeeds, the circuit closes, and the new alert follows at once.
        _recording.Delivered.Select(d => d.Notification.Title).Should().Equal(["warning", "critical-new"],
            "the new critical alert probes the channel at once, not after the circuit's 30 minutes");
        _recording.Attempts.Should().HaveCount(8);
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task CriticalEarlyProbe_PrefersTheNewCriticalAlert_OverAFreshLowerNotice()
    {
        _recording.Failing.Add("ch1");
        var alerts = Alerts();
        await Raise(alerts, "info", AlertSeverity.Info);
        _clock.Advance(TimeSpan.FromSeconds(20));
        _recording.Failing.Clear();
        await Raise(alerts, "critical", AlertSeverity.Critical);

        _recording.Delivered.Select(d => d.Notification.Title).Should().Equal(["critical"],
            "the probe takes the highest-priority eligible notice; the info notice waits for its own retry");
        await Pass(alerts, TimeSpan.FromSeconds(40));
        _recording.Delivered.Select(d => d.Notification.Title).Should().Equal("critical", "info");
    }

    [Theory]
    [InlineData(120)]
    [InlineData(300)]
    public async Task CriticalProbes_AreBoundedByTheCooldown_AndStopWhenCriticalsStop(int cooldownSeconds)
    {
        var cooldown = TimeSpan.FromSeconds(cooldownSeconds);
        _recording.Failing.Add("ch1");
        var alerts = Alerts(notifications: new NotificationOptions { CriticalProbeCooldownSeconds = cooldownSeconds });
        alerts.CriticalProbeCooldown.Should().Be(cooldown);

        // A new critical alert every 10 seconds for an hour, against a dead channel.
        var attemptTimes = new List<DateTime>();
        for (var i = 0; i < 360; i++)
        {
            var before = _recording.Attempts.Count;
            await Raise(alerts, $"critical{i}", AlertSeverity.Critical);
            await alerts.ProcessPendingAsync(Ct).Bounded();
            attemptTimes.AddRange(Enumerable.Repeat(_clock.UtcNow, _recording.Attempts.Count - before));
            _clock.Advance(TimeSpan.FromSeconds(10));
        }

        var criticalProbes = (int)(TimeSpan.FromHours(1) / cooldown);
        attemptTimes.Count.Should().BeGreaterThan(ChannelRateLimiter.WorstCaseFirstHourAttempts, "criticals probe early")
            .And.BeLessThanOrEqualTo(ChannelRateLimiter.WorstCaseFirstHourAttempts + criticalProbes);
        new ChannelRateLimiter(_clock, cooldown).MaxCriticalProbesPerHour.Should().Be(criticalProbes);

        // No more critical alerts: the last one's probe (if its cooldown had not passed yet), then
        // back to the backoff schedule (one probe every 30 minutes).
        var quiet = _recording.Attempts.Count;
        for (var i = 0; i < 4 * 60; i++)
            await Pass(alerts, TimeSpan.FromSeconds(15));
        (_recording.Attempts.Count - quiet).Should().BeInRange(1, 3);
        _recording.Delivered.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10, 60)]
    [InlineData(100_000, 3600)]
    public void CriticalProbeCooldown_IsKeptWithinItsBounds(int configured, int effective) =>
        Alerts(notifications: new NotificationOptions { CriticalProbeCooldownSeconds = configured })
            .CriticalProbeCooldown.Should().Be(TimeSpan.FromSeconds(effective));

    [Fact]
    public async Task AWarningNeverProbesEarly()
    {
        _recording.Failing.Add("ch1");
        var alerts = Alerts();
        await Raise(alerts, "first", AlertSeverity.Warning);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _recording.Failing.Clear();
        await Raise(alerts, "second", AlertSeverity.Warning);

        _recording.Attempts.Should().ContainSingle("the channel is backing off and no critical notice is waiting");
        await Pass(alerts, TimeSpan.FromSeconds(30));
        _recording.Delivered.Select(d => d.Notification.Title).Should().Equal("first", "second");
    }

    // ── D: time ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockRolledBack_TheRetryStillComesAfterItsBackoff(bool restart)
    {
        // Reviewer's probe 4: one failure, the wall clock goes back 2 hours, the agent restarts.
        // The circuit was clamped on restore, the notice's own retry was not: 31 minutes later the
        // healthy channel still had 90 minutes to wait.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Failing.Add("ch1");
        var alerts = Alerts(restart ? new SqliteAlertStateStore(path) : null);
        await Raise(alerts, "rollback", AlertSeverity.Critical);

        _clock.Skew = -TimeSpan.FromHours(2);
        _recording.Failing.Clear();
        if (restart)
        {
            alerts = Alerts(new SqliteAlertStateStore(path));
            var retry = alerts.GetPendingDeliveries().Single().ChannelRetries.Single();
            (retry.NextAttemptAt - _clock.UtcNow).Should().BeLessThanOrEqualTo(NotificationRetryPolicy.Backoff(1),
                "a persisted deadline is clamped to now + its backoff");
        }

        await Pass(alerts, TimeSpan.FromSeconds(30));
        _recording.Attempts.Should().ContainSingle("the backoff (1 minute) has not passed");
        await Pass(alerts, TimeSpan.FromSeconds(30));
        _recording.Delivered.Should().ContainSingle("the wall clock going back does not extend the wait");
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockJumpedForward_CausesNoRetryStorm(bool restart)
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var path = dir.Paths.DatabasePath;
        _recording.Failing.Add("ch1");
        var alerts = Alerts(restart ? new SqliteAlertStateStore(path) : null);
        for (var i = 0; i < 5; i++)
            await Raise(alerts, $"site{i}", AlertSeverity.Warning);
        await Pass(alerts, TimeSpan.FromMinutes(1)); // a probe: site0's retry
        _recording.Attempts.Should().HaveCount(2);

        _clock.Skew = TimeSpan.FromDays(1);
        if (restart)
            alerts = Alerts(new SqliteAlertStateStore(path));
        await Pass(alerts, TimeSpan.FromSeconds(15));
        await Pass(alerts, TimeSpan.FromSeconds(15));

        // In the process nothing is due yet (the backoff is 2 minutes); after a restart the
        // persisted deadlines have all passed, but the channel's circuit lets one probe through.
        _recording.Attempts.Should().HaveCount(restart ? 3 : 2, "a day's jump makes no notice retry on its own");
        alerts.GetPendingDeliveries().Should().HaveCount(5);
    }
}