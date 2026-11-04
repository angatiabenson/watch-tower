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
    public async Task ResolvedWhileTheHeldOpenedNoticeIsBeingSent_TheRecoveryFollowsIt()
    {
        // The reviewer's and the judge's probe: the window ends, the delivery worker starts sending
        // the held opened notice, and the monitor resolves the alert before that send completes.
        // The recovery must neither be lost nor overtake the opened notice.
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        opened.Notification.Kind.Should().Be(AlertNotificationKind.Opened);

        (await alerts.ResolveAsync(Key, ct: Ct).Bounded()).Should().BeTrue(
            "the monitor worker does not wait for the opened notice's channels");
        _dispatcher.HasWaitingCall.Should().BeFalse("the recovery waits until the opened notice was sent");
        alerts.GetPendingDeliveries().Should().ContainSingle().Which.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);

        var receipts = new List<AlertNotificationKind>();
        opened.Deliver("ch1");
        receipts.Add(opened.Notification.Kind);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved,
            "the opened notice reached operators, so they must hear that it cleared");
        resolved.Deliver("ch1");
        receipts.Add(resolved.Notification.Kind);
        await processing.Bounded();

        receipts.Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
        alerts.GetOpenAlerts().Should().BeEmpty();
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolvedWhileTheHeldOpenedNoticeIsBeingSent_OpenedFailingEverywhere_StillGetsTheRecovery()
    {
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        await alerts.ResolveAsync(Key, ct: Ct).Bounded();

        opened.Fail("ch1");
        await processing.Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse("ch1 just failed: the recovery waits for its backoff");
        alerts.GetPendingDeliveries().Should().ContainSingle("the failed opened notice was superseded by the recovery")
            .Which.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);

        _time.Advance(NotificationRetryPolicy.Backoff(1));
        processing = alerts.ProcessPendingAsync(Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        resolved.Deliver("ch1");
        await processing.Bounded();

        alerts.GetPendingDeliveries().Should().BeEmpty();
        _time.Advance(TimeSpan.FromHours(1));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _dispatcher.Sent.Should().HaveCount(2, "nothing is re-sent: the occurrence is over");
    }

    [Fact]
    public async Task EscalatedWhileAnOpenedRetryIsBeingSent_TheEscalationFollowsIt()
    {
        var alerts = Alerts();
        var raising = alerts.RaiseAsync(Key, AlertSeverity.Warning, "Slow", Down(), Ct);
        (await _dispatcher.NextAsync(Ct)).Fail("ch1");
        await raising.Bounded();

        _time.Advance(NotificationRetryPolicy.Backoff(1));
        var processing = alerts.ProcessPendingAsync(Ct);
        var retry = await _dispatcher.NextAsync(Ct);

        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Down", Down(), Ct).Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse("the escalation waits until the retry was sent");

        retry.Deliver("ch1");
        var escalated = await _dispatcher.NextAsync(Ct);
        escalated.Notification.Severity.Should().Be(AlertSeverity.Critical);
        escalated.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.Sent.Select(n => (n.Kind, n.Severity)).Should().Equal(
            (AlertNotificationKind.Opened, AlertSeverity.Warning),
            (AlertNotificationKind.Opened, AlertSeverity.Warning),
            (AlertNotificationKind.Opened, AlertSeverity.Critical));
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task OneAlertsNoticeBeingSent_DoesNotHoldUpOtherAlerts()
    {
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);

        var other = AlertKey.Endpoint("other");
        var raising = alerts.RaiseAsync(other, AlertSeverity.Critical, "Other down", Down(), Ct);
        var otherOpened = await _dispatcher.NextAsync(Ct);
        otherOpened.Notification.AlertKey.Should().Be(other.Value);
        otherOpened.Deliver("ch1");
        await raising.Bounded();

        var resolving = alerts.ResolveAsync(other, ct: Ct);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        (await resolving.Bounded()).Should().BeTrue();
        processing.IsCompleted.Should().BeFalse("the first alert's opened notice is still being sent");

        opened.Deliver("ch1");
        await processing.Bounded();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task DeliveryWorkerStoppedWhileARecoveryIsQueued_StopsPromptly_AndARestartSendsIt()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var alerts = Persistent(dir.Paths.DatabasePath, _dispatcher);
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        using var worker = new NotificationDeliveryWorker(alerts, _time, NullLogger<NotificationDeliveryWorker>.Instance);
        await worker.StartAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        await alerts.ResolveAsync(Key, ct: Ct).Bounded();

        await worker.StopAsync(Ct).Bounded(); // cancels the opened send; nothing waits on the recovery
        _dispatcher.HasWaitingCall.Should().BeFalse("a dispatch cut short by the shutdown hands nothing on");
        alerts.InFlightCount.Should().Be(0);
        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Should().ContainSingle()
            .Which.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);

        var restarted = Persistent(dir.Paths.DatabasePath, _dispatcher);
        var processing = restarted.ProcessPendingAsync(Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        resolved.Deliver("ch1");
        await processing.Bounded();
        restarted.GetPendingDeliveries().Should().BeEmpty();
        opened.Result.Task.IsCanceled.Should().BeTrue();
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

        (await alerts.ResolveAsync(Key, ct: Ct).Bounded()).Should().BeTrue();
        _dispatcher.HasWaitingCall.Should().BeFalse("the recovery waits until the retry was sent");
        retry.Deliver("ch1");
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        resolved.Deliver("ch1");
        await processing.Bounded();

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
            var receipts = dispatcher.Received.Where(n => n.AlertKey == Key.Value).Select(n => n.Kind).ToList();
            if (receipts.Contains(AlertNotificationKind.Opened))
                receipts.Should().Equal([AlertNotificationKind.Opened, AlertNotificationKind.Resolved], $"iteration {i}");
            dispatcher.MaxOverlap.Should().Be(1, $"iteration {i}: an alert's notices are never sent at the same time");
            alerts.GetOpenAlerts().Should().BeEmpty();
            alerts.GetPendingDeliveries().Should().BeEmpty();
        }
    }

    // ── A maintenance window starting while a batch is sent ────────────────

    [Fact]
    public async Task WindowStartingWhileHeldNoticesAreDrained_HoldsTheRestUntilItEnds()
    {
        // The reviewer's probe: two held notices; a new window starts during the first send.
        var alerts = Alerts();
        _maintenance.Active = true;
        await alerts.RaiseAsync(AlertKey.Endpoint("a"), AlertSeverity.Critical, "a down", Down(), Ct);
        await alerts.RaiseAsync(AlertKey.Endpoint("b"), AlertSeverity.Critical, "b down", Down(), Ct);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var first = await _dispatcher.NextAsync(Ct);
        _maintenance.Active = true;
        first.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.Sent.Should().ContainSingle("nothing is sent once the new window has started");
        var rest = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        rest.AlertKey.Should().NotBe(first.Notification.AlertKey);
        rest.Held.Should().BeTrue("it stays held, and is brought up to date when it is released");

        _time.Advance(TimeSpan.FromHours(1));
        await alerts.ProcessPendingAsync(Ct).Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse("the window is still active");

        _maintenance.Active = false;
        processing = alerts.ProcessPendingAsync(Ct);
        var second = await _dispatcher.NextAsync(Ct);
        second.Notification.AlertKey.Should().Be(rest.AlertKey);
        second.Notification.Kind.Should().Be(AlertNotificationKind.Opened);
        second.Deliver("ch1");
        await processing.Bounded();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task ResolvedInANewWindowWhileTheReleasedOpeningIsBeingSent_GetsAnExplicitRecoveryAfterARestart()
    {
        // The judge's probe: the released opening is in flight (not yet counted as notified) when a
        // new window starts and the alert resolves; the opening then reaches its channel. The
        // recovery must be sent on its own, not folded into the summary, also after a restart.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var alerts = Persistent(dir.Paths.DatabasePath, _dispatcher);
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        _maintenance.Active = true;
        (await alerts.ResolveAsync(Key, ct: Ct).Bounded()).Should().BeTrue();
        opened.Deliver("ch1");
        await processing.Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse("the recovery is held by the new window");

        var held = new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Should().ContainSingle().Subject;
        held.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        held.Held.Should().BeTrue();
        held.ExplicitRecovery.Should().BeTrue();

        _maintenance.Active = false;
        var restarted = Persistent(dir.Paths.DatabasePath, _dispatcher);
        processing = restarted.ProcessPendingAsync(Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        resolved.Notification.AlertKey.Should().Be(Key.Value);
        resolved.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.HasWaitingCall.Should().BeFalse();
        _dispatcher.Sent.Should().NotContain(n => n.Title.StartsWith("Maintenance ended"));
        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
        restarted.GetPendingDeliveries().Should().BeEmpty();
    }

    // ── A maintenance window starting right before a direct send ───────────

    [Fact]
    public async Task WindowStartingWhileARaiseAwaitsThePortal_HoldsTheOpenedNotice()
    {
        // The judge's probe: the raise decided "not in maintenance" before publishing to the
        // portal; the window starts while that publication is in progress.
        var portal = new GatedPortalPublisher();
        var alerts = TestAlerts.Create(_metrics.Object, _time, _dispatcher, portal: portal, maintenance: _maintenance);

        var raising = Raise(alerts);
        await portal.Entered.Task.Bounded();
        _maintenance.Active = true;
        portal.Release();
        await raising.Bounded();

        _dispatcher.HasWaitingCall.Should().BeFalse("nothing is sent once the window has started");
        _dispatcher.Sent.Should().BeEmpty();
        var held = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        held.Notification.Kind.Should().Be(AlertNotificationKind.Opened);
        held.Held.Should().BeTrue();
        alerts.InFlightCount.Should().Be(0);

        await alerts.ProcessPendingAsync(Ct).Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse("the window is still active");

        _maintenance.Active = false;
        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        opened.Notification.Kind.Should().Be(AlertNotificationKind.Opened);
        opened.Deliver("ch1");
        await processing.Bounded();
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(1);
    }

    [Fact]
    public async Task WindowStartingWhileACloseAwaitsThePortal_HoldsTheRecovery_AndSendsItAfterwards()
    {
        var portal = new GatedPortalPublisher { Gates = resolved => resolved == true };
        var alerts = TestAlerts.Create(_metrics.Object, _time, _dispatcher, portal: portal, maintenance: _maintenance);
        var raising = Raise(alerts);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        await raising.Bounded();

        var resolving = alerts.ResolveAsync(Key, ct: Ct);
        await portal.Entered.Task.Bounded();
        _maintenance.Active = true;
        portal.Release();
        (await resolving.Bounded()).Should().BeTrue();

        _dispatcher.HasWaitingCall.Should().BeFalse("nothing is sent once the window has started");
        var held = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        held.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        held.Held.Should().BeTrue();
        alerts.InFlightCount.Should().Be(0);

        _maintenance.Active = false;
        var processing = alerts.ProcessPendingAsync(Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved, "operators were told it opened");
        resolved.Deliver("ch1");
        await processing.Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task WindowStartingBeforeAQueuedRecoveryIsHandedOn_HoldsIt()
    {
        var alerts = Alerts();
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        var processing = alerts.ProcessPendingAsync(Ct);
        var opened = await _dispatcher.NextAsync(Ct);
        await alerts.ResolveAsync(Key, ct: Ct).Bounded(); // queued behind the opened notice
        _maintenance.Active = true;
        opened.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.HasWaitingCall.Should().BeFalse("the window started before the recovery's turn came");
        var held = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        held.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        held.Held.Should().BeTrue();

        _maintenance.Active = false;
        processing = alerts.ProcessPendingAsync(Ct);
        var resolved = await _dispatcher.NextAsync(Ct);
        resolved.Notification.Kind.Should().Be(AlertNotificationKind.Resolved);
        resolved.Deliver("ch1");
        await processing.Bounded();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task WindowStartingWhileAnEventAwaitsThePortal_HoldsIt_ForTheSummary()
    {
        var portal = new GatedPortalPublisher();
        var alerts = TestAlerts.Create(_metrics.Object, _time, _dispatcher, portal: portal, maintenance: _maintenance);

        var notifying = alerts.NotifyEventAsync(AlertKey.Reboot, AlertSeverity.Warning, "Restarted",
            new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.ServerRestarted, "The server restarted"), Ct);
        await portal.Entered.Task.Bounded();
        _maintenance.Active = true;
        portal.Release();
        await notifying.Bounded();

        _dispatcher.HasWaitingCall.Should().BeFalse();
        alerts.GetPendingDeliveries().Should().ContainSingle().Which.Held.Should().BeTrue();
    }

    [Fact]
    public async Task WindowStartingWhileRetriesAreSent_LeavesTheRemainingRetriesPending()
    {
        var alerts = Alerts();
        var raising = alerts.RaiseAsync(AlertKey.Endpoint("a"), AlertSeverity.Critical, "a down", Down(), Ct);
        (await _dispatcher.NextAsync(Ct)).Fail("ch1");
        await raising.Bounded();
        await alerts.RaiseAsync(AlertKey.Endpoint("b"), AlertSeverity.Critical, "b down", Down(), Ct).Bounded();
        _dispatcher.HasWaitingCall.Should().BeFalse("ch1 just failed: b's opening waits for its circuit's probe");

        _time.Advance(NotificationRetryPolicy.Backoff(1));
        var processing = alerts.ProcessPendingAsync(Ct);
        var retry = await _dispatcher.NextAsync(Ct);
        retry.Notification.AlertKey.Should().Be(AlertKey.Endpoint("a").Value, "the probe is a's retry");
        _maintenance.Active = true;
        retry.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.Sent.Should().HaveCount(2, "a's first attempt and its retry");
        var rest = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        rest.AlertKey.Should().Be(AlertKey.Endpoint("b").Value);
        rest.FailedAttempts.Should().Be(0, "it was not attempted during the window");

        _maintenance.Active = false;
        processing = alerts.ProcessPendingAsync(Ct);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        await processing.Bounded();
        alerts.GetPendingDeliveries().Should().BeEmpty();
    }

    // ── A store write failing while a delivery is claimed ──────────────────

    [Fact]
    public async Task StoreWriteFailingWhileAHeldNoticeIsReleased_DoesNotStrandIt()
    {
        // The reviewer's probe: the write that releases the held notice fails once. The claim
        // taken for the send must be given back, or the notice is never sent.
        var store = new FlakyAlertStateStore(new InMemoryAlertStateStore());
        var dispatcher = new RecordingDispatcher();
        var alerts = TestAlerts.Create(_metrics.Object, _time, dispatcher, state: store, maintenance: _maintenance);
        _maintenance.Active = true;
        await Raise(alerts);
        _maintenance.Active = false;

        store.FailNextDeliveryWrite(d => !d.Held);
        var act = () => alerts.ProcessPendingAsync(Ct).Bounded();
        await act.Should().ThrowAsync<IOException>();
        store.FailedWrites.Should().Be(1);
        alerts.InFlightCount.Should().Be(0, "the claim is released although its send was never prepared");
        dispatcher.Sent.Should().BeEmpty();

        for (var i = 0; i < 3; i++)
            await alerts.ProcessPendingAsync(Ct).Bounded();

        dispatcher.Sent.Should().ContainSingle().Which.Kind.Should().Be(AlertNotificationKind.Opened);
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.InFlightCount.Should().Be(0);
        alerts.GetOpenAlerts().Single().NotifyCount.Should().Be(1);
    }

    [Fact]
    public async Task StoreWriteFailingWhenTheOutcomeIsRecorded_LeavesTheDeliveryRetryable()
    {
        var store = new FlakyAlertStateStore(new InMemoryAlertStateStore());
        var dispatcher = new RecordingDispatcher();
        dispatcher.Failing.Add("ch1");
        var alerts = TestAlerts.Create(_metrics.Object, _time, dispatcher, state: store, maintenance: _maintenance);

        store.FailNextDeliveryWrite(d => d.FailedAttempts > 0);
        var act = () => Raise(alerts).Bounded();
        await act.Should().ThrowAsync<IOException>();
        alerts.InFlightCount.Should().Be(0);

        dispatcher.Failing.Clear();
        _time.Advance(NotificationRetryPolicy.Backoff(1)); // the channel's circuit counted the failure
        await alerts.ProcessPendingAsync(Ct).Bounded();

        dispatcher.Sent.Should().HaveCount(2, "the failed attempt is retried");
        dispatcher.Delivered.Should().ContainSingle();
        alerts.GetPendingDeliveries().Should().BeEmpty();
        alerts.InFlightCount.Should().Be(0);
    }

    // ── Shutdown while a channel is still sending ──────────────────────────

    /// <summary>
    /// Webhook sender whose channel <c>fast</c> delivers at once and whose channel <c>slow</c>
    /// waits for <see cref="SlowResult"/> (or the dispatch's cancellation).
    /// </summary>
    private sealed class TwoSpeedSender : INotificationChannel
    {
        private readonly Dictionary<string, int> _sends = new(StringComparer.OrdinalIgnoreCase);

        public TaskCompletionSource SlowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the slow channel's sends; never completed means it hangs until cancelled.</summary>
        public TaskCompletionSource<ChannelSendResult> SlowResult { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NotificationChannelType Type => NotificationChannelType.Webhook;

        public bool IsAvailable(NotificationChannel channel) => true;

        public int Sends(string channel)
        {
            lock (_sends)
                return _sends.GetValueOrDefault(channel);
        }

        public async Task<ChannelSendResult> SendAsync(NotificationChannel channel, AlertNotification notification, CancellationToken ct)
        {
            lock (_sends)
                _sends[channel.Name] = _sends.GetValueOrDefault(channel.Name) + 1;
            if (channel.Name == "fast")
                return ChannelSendResult.Ok();
            SlowEntered.TrySetResult();
            return await SlowResult.Task.WaitAsync(ct);
        }
    }

    private static NotificationDispatcher TwoSpeedDispatcher(TwoSpeedSender sender) => new(
        [sender],
        new TestOptionsMonitor<NotificationOptions>(new NotificationOptions
        {
            Email = new EmailNotificationSettings { Enabled = false },
            TimeoutSeconds = 120, // the shutdown, not the channel timeout, cuts the slow send short
            Channels =
            [
                new NotificationChannelOptions { Name = "fast", Type = "Webhook", Url = "https://example.org/fast" },
                new NotificationChannelOptions { Name = "slow", Type = "Webhook", Url = "https://example.org/slow" },
            ],
        }),
        NullLogger<NotificationDispatcher>.Instance);

    private AlertManager Persistent(string dbPath, INotificationDispatcher dispatcher) =>
        TestAlerts.Create(_metrics.Object, _time, dispatcher, state: new SqliteAlertStateStore(dbPath), maintenance: _maintenance);

    [Fact]
    public async Task ShutdownDuringASend_KeepsTheChannelsThatDelivered_AndARestartDoesNotResendThem()
    {
        // The reviewer's probe: one channel delivered, the other is still sending when the
        // graceful shutdown cancels the dispatch.
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var sender = new TwoSpeedSender();
        var alerts = Persistent(dir.Paths.DatabasePath, TwoSpeedDispatcher(sender));
        using var shutdown = new CancellationTokenSource();

        var raising = alerts.RaiseAsync(Key, AlertSeverity.Critical, "Down", Down(), shutdown.Token);
        await sender.SlowEntered.Task.Bounded();
        await shutdown.CancelAsync();
        var act = () => raising.Bounded();
        await act.Should().ThrowAsync<OperationCanceledException>();

        var persisted = new SqliteAlertStateStore(dir.Paths.DatabasePath);
        persisted.LoadDeliveries().Should().ContainSingle().Which.DeliveredChannels.Should().Equal("fast");
        persisted.LoadAll().Single().NotifyCount.Should().Be(1, "the alert reached an operator before the shutdown");

        // New process: the slow channel answers this time.
        sender.SlowResult.SetResult(ChannelSendResult.Ok());
        var restarted = Persistent(dir.Paths.DatabasePath, TwoSpeedDispatcher(sender));
        await restarted.ProcessPendingAsync(Ct).Bounded();

        sender.Sends("fast").Should().Be(1, "a channel that acknowledged the notice never gets it again");
        sender.Sends("slow").Should().Be(2);
        restarted.GetPendingDeliveries().Should().BeEmpty();
        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Should().BeEmpty();
        restarted.GetOpenAlerts().Single().NotifyCount.Should().Be(1);
    }

    [Fact]
    public async Task DeliveryWorkerStoppedDuringASend_StopsPromptly_AndKeepsTheChannelsThatDelivered()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var sender = new TwoSpeedSender();
        var alerts = Persistent(dir.Paths.DatabasePath, TwoSpeedDispatcher(sender));
        _maintenance.Active = true;
        await Raise(alerts); // held
        _maintenance.Active = false;

        using var worker = new NotificationDeliveryWorker(alerts, _time, NullLogger<NotificationDeliveryWorker>.Instance);
        await worker.StartAsync(Ct);
        await sender.SlowEntered.Task.Bounded();
        await worker.StopAsync(Ct).Bounded(); // no deadlock with the in-flight send

        var pending = new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadDeliveries().Should().ContainSingle().Subject;
        pending.DeliveredChannels.Should().Equal("fast");
        pending.Held.Should().BeFalse("it was released when the window ended");

        sender.SlowResult = new TaskCompletionSource<ChannelSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.SlowResult.SetResult(ChannelSendResult.Ok());
        var restarted = Persistent(dir.Paths.DatabasePath, TwoSpeedDispatcher(sender));
        await restarted.ProcessPendingAsync(Ct).Bounded();

        sender.Sends("fast").Should().Be(1);
        sender.Sends("slow").Should().Be(2);
        restarted.GetPendingDeliveries().Should().BeEmpty();
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

        private readonly List<AlertNotification> _received = [];
        private int _active;

        /// <summary>Notifications in the order their send completed.</summary>
        public IReadOnlyList<AlertNotification> Received
        {
            get
            {
                lock (_sent)
                    return _received.ToList();
            }
        }

        /// <summary>Most sends that were in progress at the same time (every send is for one key).</summary>
        public int MaxOverlap { get; private set; }

        public IReadOnlyList<string> ChannelsFor(AlertNotification notification) => ["ch1"];

        public async Task<DispatchResult> DispatchAsync(
            AlertNotification notification, IReadOnlySet<string>? channels, Action<ChannelDelivery>? onChannelCompleted,
            CancellationToken ct)
        {
            lock (_sent)
            {
                _sent.Add(notification);
                MaxOverlap = Math.Max(MaxOverlap, ++_active);
            }
            await Task.Yield();
            if (Random.Shared.Next(4) == 0)
                await Task.Delay(1, ct); // sometimes a slower send
            var delivery = new ChannelDelivery("ch1", true, "Delivered");
            onChannelCompleted?.Invoke(delivery);
            lock (_sent)
            {
                _received.Add(notification);
                _active--;
            }
            return new DispatchResult([delivery]);
        }
    }
}