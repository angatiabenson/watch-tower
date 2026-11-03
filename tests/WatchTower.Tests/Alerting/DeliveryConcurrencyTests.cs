using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Tests.Alerting;

/// <summary>
/// Deliveries that are in flight while the monitor workers raise or resolve the same alert. Every
/// send is held by a <see cref="GatedDispatcher"/> until the test completes it, so each
/// interleaving is reproduced exactly rather than by timing.
/// </summary>
public class DeliveryConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly GatedDispatcher _dispatcher = new();
    private readonly TestMaintenance _maintenance = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static readonly AlertKey Key = AlertKey.Endpoint("race");

    private AlertManager Alerts(AlertingOptions? options = null, IAlertStateStore? state = null) =>
        TestAlerts.Create(_metrics.Object, _time, _dispatcher, options, state: state, maintenance: _maintenance);

    private Incident Down() => new(_time.GetUtcNow().UtcDateTime, IncidentType.EndpointDown, "https://example.org is down");

    private Task Raise(AlertManager alerts) => alerts.RaiseAsync(Key, AlertSeverity.Critical, "Down", Down(), Ct);

    [Fact]
    public async Task ResolvedWhileTheHeldOpenedNoticeIsBeingSent_TheRecoveryIsSentToo()
    {
        // The reviewer's probe: the window ends, the delivery worker starts sending the held opened
        // notice, and the monitor resolves the alert before that send completes.
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        opened.Notification.Kind.Should().Be(AlertNotificationKind.Opened);

        var resolving = alerts.ResolveAsync(Key, ct: Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved,
            "the opened notice may reach operators at any moment, so they must hear that it cleared");

        opened.Deliver("ch1");
        await processing.Bounded();
        resolved.Deliver("ch1");
        (await resolving.Bounded()).Should().BeTrue();

        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
        alerts.GetOpenAlerts().Should().BeEmpty();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ResolvedWhileTheHeldOpenedNoticeIsBeingSent_RecoveryCompletingFirst_LeavesNothingBehind()
    {
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        var resolving = alerts.ResolveAsync(Key, ct: Ct);
        var resolved = await _dispatcher.NextAsync(Ct);

        resolved.Deliver("ch1");
        await resolving.Bounded();
        opened.Deliver("ch1");
        await processing.Bounded();

        alerts.GetPendingDeliveries().Should().BeEmpty("the opened notice was superseded by the recovery");
        alerts.GetOpenAlerts().Should().BeEmpty();
        _dispatcher.HasWaitingCall.Should().BeFalse();

        // Nothing is re-sent later: the occurrence is over.
        _time.Advance(TimeSpan.FromHours(1));
        await alerts.ProcessPendingAsync(Ct);
        _dispatcher.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolvedBeforeTheDeliveryWorkerTakesTheHeldNotice_TheOpenedNoticeIsNeverSent()
    {
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        await alerts.ResolveAsync(Key, ct: Ct);
        var processing = alerts.ProcessPendingAsync(Ct);
        var summary = await _dispatcher.NextAsync(Ct);
        summary.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.Sent.Should().ContainSingle().Which.Title.Should().StartWith("Maintenance ended",
            "an alert that opened and cleared inside the window is only summarised");
    }

    [Fact]
    public async Task OpenedNoticeSentBeforeTheResolution_GetsTheRecovery()
    {
        var alerts = Alerts();
        var raising = Raise(alerts);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        await raising.Bounded();

        var resolving = alerts.ResolveAsync(Key, ct: Ct);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        await resolving.Bounded();

        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
    }

    [Fact]
    public async Task RetryInFlight_ResolvedMeanwhile_RecoveryIsSent()
    {
        var alerts = Alerts();
        var raising = Raise(alerts);
        (await _dispatcher.NextAsync(Ct)).Fail("ch1");
        await raising.Bounded();

        _time.Advance(NotificationRetryPolicy.Backoff(1));
        var processing = alerts.ProcessPendingAsync(Ct);
        var retry = await _dispatcher.NextAsync(Ct);

        var resolving = alerts.ResolveAsync(Key, ct: Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        retry.Deliver("ch1");
        await processing.Bounded();
        resolved.Deliver("ch1");
        await resolving.Bounded();

        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(
            AlertNotificationKind.Opened, AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task RaiseAndResolveOfOneKey_AreSerialised_SoAResolvedAlertIsNeverReopened()
    {
        var alerts = Alerts(new AlertingOptions { RenotifyMinutes = 60 });
        var raising = Raise(alerts);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        await raising.Bounded();
        _time.Advance(TimeSpan.FromMinutes(61));

        // A reminder is due; its incident detail is still being gathered when the alert resolves.
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reminding = alerts.RaiseAsync(Key, AlertSeverity.Critical, "Down", async _ =>
        {
            building.SetResult();
            await release.Task;
            return Down();
        }, Ct);
        await building.Task.Bounded();

        var resolving = alerts.ResolveAsync(Key, ct: Ct);
        resolving.IsCompleted.Should().BeFalse("closing waits for the raise of the same key");

        release.SetResult();
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1"); // the reminder
        await reminding.Bounded();
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1"); // the recovery
        (await resolving.Bounded()).Should().BeTrue();

        alerts.IsOpen(Key).Should().BeFalse();
        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(
            AlertNotificationKind.Opened, AlertNotificationKind.Reminder, AlertNotificationKind.Resolved);
    }

    [Fact]
    public async Task ConcurrentResolutionAndDelivery_NeverLeaveAnOpenedNoticeWithoutItsRecovery()
    {
        // Free-running interleavings of the same race. Whatever the order, an opened notice that
        // was delivered is always followed by a resolved one.
        for (var i = 0; i < 200; i++)
        {
            var dispatcher = new AutoDispatcher();
            var maintenance = new TestMaintenance { Active = true };
            var alerts = TestAlerts.Create(_metrics.Object, TimeProvider.System, dispatcher, maintenance: maintenance);
            await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Down", Down(), Ct);
            maintenance.Active = false;

            using var start = new Barrier(2);
            var deliver = Task.Run(async () =>
            {
                start.SignalAndWait(Ct);
                await alerts.ProcessPendingAsync(Ct);
            }, Ct);
            var resolve = Task.Run(async () =>
            {
                start.SignalAndWait(Ct);
                await alerts.ResolveAsync(Key, ct: Ct);
            }, Ct);
            await Task.WhenAll(deliver, resolve).Bounded();
            // Resolved after the pass collected its work: summarised on the worker's next tick.
            await alerts.ProcessPendingAsync(Ct).Bounded();

            var kinds = dispatcher.Sent.Select(n => n.Kind).ToList();
            if (kinds.Contains(AlertNotificationKind.Opened))
                kinds.Should().Contain(AlertNotificationKind.Resolved, $"iteration {i} sent {string.Join(",", kinds)}");
            alerts.GetOpenAlerts().Should().BeEmpty();
            alerts.GetPendingDeliveries().Should().BeEmpty();
        }
    }

    /// <summary>Delivers to <c>ch1</c> after yielding, so sends overlap with the caller's next step.</summary>
    private sealed class AutoDispatcher : INotificationDispatcher
    {
        private readonly List<AlertNotification> _sent = [];

        public IReadOnlyList<AlertNotification> Sent
        {
            get
            {
                lock (_sent)
                    return _sent.ToList();
            }
        }

        public async Task<DispatchResult> DispatchAsync(AlertNotification notification, IReadOnlySet<string>? skipChannels, CancellationToken ct)
        {
            lock (_sent)
                _sent.Add(notification);
            await Task.Yield();
            return new DispatchResult([new ChannelDelivery("ch1", true, "Delivered")]);
        }
    }
}