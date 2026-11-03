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
    public async Task WindowStartingWhileRetriesAreSent_LeavesTheRemainingRetriesPending()
    {
        var alerts = Alerts();
        foreach (var name in new[] { "a", "b" })
        {
            var raising = alerts.RaiseAsync(AlertKey.Endpoint(name), AlertSeverity.Critical, $"{name} down", Down(), Ct);
            (await _dispatcher.NextAsync(Ct)).Fail("ch1");
            await raising.Bounded();
        }

        _time.Advance(NotificationRetryPolicy.Backoff(1));
        var processing = alerts.ProcessPendingAsync(Ct);
        var retry = await _dispatcher.NextAsync(Ct);
        _maintenance.Active = true;
        retry.Deliver("ch1");
        await processing.Bounded();

        _dispatcher.Sent.Should().HaveCount(3, "two first attempts and one retry");
        var rest = alerts.GetPendingDeliveries().Should().ContainSingle().Subject;
        rest.FailedAttempts.Should().Be(1, "it was not attempted during the window");

        _maintenance.Active = false;
        processing = alerts.ProcessPendingAsync(Ct);
        (await _dispatcher.NextAsync(Ct)).Deliver("ch1");
        await processing.Bounded();
        alerts.GetPendingDeliveries().Should().BeEmpty();
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

        public async Task<DispatchResult> DispatchAsync(
            AlertNotification notification, IReadOnlySet<string>? skipChannels, Action<ChannelDelivery>? onChannelCompleted,
            CancellationToken ct)
        {
            lock (_sent)
                _sent.Add(notification);
            await Task.Yield();
            var delivery = new ChannelDelivery("ch1", true, "Delivered");
            onChannelCompleted?.Invoke(delivery);
            return new DispatchResult([delivery]);
        }
    }
}