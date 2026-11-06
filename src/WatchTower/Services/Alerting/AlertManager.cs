using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services.Alerting;

/// <inheritdoc cref="IAlertManager"/>
public sealed class AlertManager : IAlertManager
{
    private readonly IAlertStateStore _store;
    private readonly IMetricsStore _metricsStore;
    private readonly IPortalEventPublisher _portal;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IOptionsMonitor<AlertingOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertManager> _logger;
    private readonly IMaintenanceState _maintenance;
    private readonly string _server;

    /// <summary>
    /// Guards the in-memory alert states, pending deliveries and in-flight claims. Never held
    /// across an await: every decision that depends on more than one of them (closing an alert
    /// while its notice is being sent, claiming a delivery, recording a channel) is made in one
    /// section, so the delivery worker and the monitor workers always see a consistent picture.
    /// </summary>
    private readonly Lock _gate = new();
    private Dictionary<string, AlertState>? _states;
    private Dictionary<string, PendingDelivery>? _deliveries;

    /// <summary>Last <see cref="PendingDelivery.Sequence"/> handed out (guarded by <see cref="_gate"/>).</summary>
    private long _lastSequence;

    /// <summary>
    /// Deliveries being sent right now (by id), so no two callers send one at once. Guarded by
    /// <see cref="_gate"/>: a claim is taken in the same section that reads the delivery, and
    /// released only after its outcome (including <see cref="MarkNotified"/>) is recorded.
    /// </summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// The opened, reminder or resolved notice being sent right now for each alert key (guarded by
    /// <see cref="_gate"/>). An alert's notices are sent one dispatch at a time, and each channel
    /// gets them in queue order (see <see cref="ClaimNextLocked"/>): nothing else of the alert is
    /// claimed while one of its notices is in flight, and the sender of that notice schedules again
    /// as soon as its dispatch has finished (see <see cref="PumpAsync"/>). Nobody waits for it, so
    /// a slow channel never blocks a monitor worker, other alerts or a shutdown.
    /// </summary>
    private readonly Dictionary<string, PendingDelivery> _sending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each notification channel's circuit and attempt budget, across every alert key (guarded by
    /// <see cref="_gate"/>, loaded lazily from <see cref="ChannelCircuitsKey"/>). See
    /// <see cref="ChannelRateLimiter"/>: a channel that is down costs a bounded number of attempts
    /// however many notices wait for it.
    /// </summary>
    private ChannelRateLimiter? _limiter;

    /// <summary><c>agent_state</c> key of the persisted channel circuits.</summary>
    internal const string ChannelCircuitsKey = "notification_channel_circuits";

    /// <summary>
    /// Aging: a notice waiting for a channel gains one priority level (Info → Warning → Critical)
    /// for every interval it has waited, so under a constant stream of critical notices a lower
    /// one still goes out: after at most two intervals it competes as critical, and then by age.
    /// </summary>
    internal static readonly TimeSpan AgingInterval = TimeSpan.FromMinutes(5);

    /// <summary>Default of <c>Notifications:CriticalProbeCooldownSeconds</c>.</summary>
    internal static readonly TimeSpan DefaultCriticalProbeCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Shortest critical-probe cooldown (a smaller setting is raised to it).</summary>
    internal static readonly TimeSpan MinCriticalProbeCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A delivery claimed for one dispatch, and the channels it goes to now, each with the
    /// <see cref="ChannelRateLimiter.Lease"/> that allowed it.
    /// </summary>
    private sealed record Claim(PendingDelivery Delivery, IReadOnlyList<ChannelRateLimiter.Lease> Leases);

    /// <summary>
    /// Most notices queued for one alert key. An alert that keeps opening and clearing while its
    /// notices cannot go out (a channel down, a slow channel, a maintenance window) leaves one
    /// recovery per occurrence whose opening may have reached operators; without a bound that
    /// queue grows with every flap. When a new notice would exceed it, the oldest
    /// <see cref="KeptOldestNotices"/> notices are kept as they are (the earliest occurrences, whose
    /// recoveries operators are most likely waiting for), the new notice is the alert's latest
    /// state, and everything in between is folded into one persisted resolved notice titled
    /// "Flapping: N occurrences between T1 and T2" (merged with an earlier flapping notice of the
    /// key, if there is one). See <see cref="CoalesceLocked"/>.
    /// </summary>
    internal const int MaxQueuedNotices = 5;

    /// <summary>Oldest notices of a key that are never folded into a flapping notice.</summary>
    internal const int KeptOldestNotices = MaxQueuedNotices - 2;

    /// <summary>
    /// Serialises raising and closing per alert key, so the open/resolved lifecycle of one key is
    /// never interleaved (e.g. a retirement and a raise of the same key). Deliveries never take it,
    /// and no code path takes two, so it cannot deadlock with the delivery worker. A key's lock
    /// only exists while it is held or waited for.
    /// </summary>
    private readonly KeyedAsyncLock _keyLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps <see cref="ProcessPendingAsync"/> runs from overlapping.</summary>
    private readonly SemaphoreSlim _processGate = new(1, 1);

    private readonly IOptionsMonitor<NotificationOptions>? _notificationOptions;

    public AlertManager(
        IAlertStateStore store,
        IMetricsStore metricsStore,
        IPortalEventPublisher portal,
        INotificationDispatcher dispatcher,
        IOptionsMonitor<AlertingOptions> options,
        TimeProvider time,
        ILogger<AlertManager> logger,
        IMaintenanceState? maintenance = null,
        string? serverName = null,
        IOptionsMonitor<NotificationOptions>? notificationOptions = null,
        PendingAlertTracker? pendingAlerts = null)
    {
        _store = store;
        _metricsStore = metricsStore;
        _portal = portal;
        _dispatcher = dispatcher;
        _options = options;
        _time = time;
        _logger = logger;
        _maintenance = maintenance ?? NoMaintenance.Instance;
        _server = serverName ?? Environment.MachineName;
        _notificationOptions = notificationOptions;
        _pending = pendingAlerts ?? new PendingAlertTracker();
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// <c>Notifications:CriticalProbeCooldownSeconds</c> (default 120, at least 60): how often a
    /// new critical notice may probe a channel whose circuit is open (see <see cref="ChannelRateLimiter"/>).
    /// </summary>
    internal TimeSpan CriticalProbeCooldown =>
        _notificationOptions is { } notifications
            ? TimeSpan.FromSeconds(Math.Clamp(notifications.CurrentValue.CriticalProbeCooldownSeconds,
                MinCriticalProbeCooldown.TotalSeconds, MaxCriticalProbeCooldown.TotalSeconds))
            : DefaultCriticalProbeCooldown;

    /// <summary>Longest critical-probe cooldown (a larger setting is lowered to it).</summary>
    internal static readonly TimeSpan MaxCriticalProbeCooldown = TimeSpan.FromHours(1);

    public TimeSpan RecoveryWindow => TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.ResolveAfterSeconds));

    public bool IsOpen(AlertKey key) => TryGet(key) is { IsOpen: true };

    /// <summary>Waiting periods (see <see cref="ObserveBreach"/>); in memory, so a restart starts them over.</summary>
    private readonly PendingAlertTracker _pending;

    public bool ObserveBreach(AlertKey key, AlertSeverity severity, TimeSpan alertAfter, string title) =>
        _pending.Observe(key, severity, alertAfter, title, UtcNow, TryGet(key) is { IsOpen: true } open ? open.Severity : null);

    public void ClearBreach(AlertKey key) => _pending.Clear(key, UtcNow);

    public void ForgetBreach(AlertKey key) => _pending.Forget(key);

    public IReadOnlyList<PendingAlert> GetPendingAlerts() => _pending.Snapshot();

    public IReadOnlyList<ShortBreachSummary> GetShortBreaches(DateTime sinceUtc) => _pending.ShortBreaches(sinceUtc);

    public IReadOnlyList<AlertState> GetOpenAlerts()
    {
        lock (_gate)
            return States().Values.Where(s => s.IsOpen).OrderBy(s => s.OpenedAt).ToList();
    }

    public IReadOnlyList<PendingDelivery> GetPendingDeliveries()
    {
        lock (_gate)
            return Deliveries().Values.OrderBy(d => d.Sequence).ToList();
    }

    /// <summary>Alert keys whose lifecycle lock is held or waited for right now (tests).</summary>
    internal int KeyLockCount => _keyLocks.Count;

    /// <summary>Deliveries claimed by a sender right now (tests).</summary>
    internal int InFlightCount
    {
        get
        {
            lock (_gate)
                return _inFlight.Count;
        }
    }

    public async Task RaiseAsync(
        AlertKey key,
        AlertSeverity severity,
        string title,
        Func<CancellationToken, Task<Incident>> incidentFactory,
        CancellationToken ct = default)
    {
        using var keyLock = await LockKeyAsync(key, ct);

        var now = UtcNow;
        var options = _options.CurrentValue;
        var existing = TryGet(key);
        var open = existing is { IsOpen: true };
        var escalated = open && severity > existing!.Severity;

        var inMaintenance = _maintenance.Current.Active;

        if (open && !escalated)
        {
            _pending.Raised(key, severity); // nothing left to wait for at this severity
            var pending = PendingNotice(key.Value, existing!.Occurrence);
            var reminderDue = IsReminderDue(existing, now, options);
            // A notice still on its way (held, or not yet delivered anywhere) is finished rather
            // than replaced. One that reached some channels is replaced by the next reminder, so a
            // permanently failing channel cannot suppress reminders to the working ones. It may be
            // queued behind an earlier occurrence's recovery or other channels' work: whatever is
            // due is scheduled.
            if (pending is not null && (pending.Held || pending.DeliveredChannels.Count == 0 || !reminderDue))
            {
                if (!inMaintenance)
                    await PumpAsync(trigger: null, ct);
                return;
            }
            if (!reminderDue)
                return; // already open and notified: stay quiet
        }

        var incident = await incidentFactory(ct);
        var isNew = !open || escalated;

        AlertState state;
        lock (_gate)
        {
            // Re-read: a delivery may have counted a notification while the incident was built.
            var current = States().GetValueOrDefault(key.Value);
            if (!open || current is not { IsOpen: true })
            {
                // A new occurrence: the notices of earlier ones (their recoveries) stay queued.
                state = new AlertState(key.Value, severity, incident.Type, title, now,
                    LastNotifiedAt: null, NotifyCount: 0, ResolvedAt: null, incident.Description,
                    Occurrence: (current?.Occurrence ?? 0) + 1);
            }
            else
            {
                state = current with
                {
                    Severity = escalated ? severity : current.Severity,
                    IncidentType = escalated ? incident.Type : current.IncidentType,
                    Title = title,
                    LastDescription = incident.Description,
                };
            }

            // Persist before notifying: a crash mid-send must not re-open (and re-send) the alert.
            Save(state);
        }

        // Raised: the waiting period (if any) is over for this severity and the ones below it.
        _pending.Raised(key, state.Severity);

        if (isNew)
        {
            _metricsStore.AddIncident(incident);
            await _portal.PublishAlertEventAsync(incident, key.Value, resolved: false, ct);
            _logger.LogWarning("Alert {Key} {Action} ({Severity}): {Description}",
                key.Value, escalated ? "escalated" : "opened", severity, incident.Description);
        }

        // A still-pending alert (never notified) announces itself as opened, not as a reminder.
        var notification = new AlertNotification(
            isNew || state.NotifyCount == 0 ? AlertNotificationKind.Opened : AlertNotificationKind.Reminder,
            key.Value,
            state.Severity,
            title,
            incident.Description,
            _server,
            now,
            OpenedAtUtc: state.OpenedAt,
            Duration: now - state.OpenedAt,
            Incident: incident,
            NotifyCount: state.NotifyCount + 1);

        await DeliverAsync(notification, state.Occurrence, now, inMaintenance, ct);
    }

    public Task<bool> ResolveAsync(AlertKey key, Incident? recoveryIncident = null, CancellationToken ct = default) =>
        CloseAsync(key, recoveryIncident, retiredReason: null, ct);

    public Task<bool> RetireAsync(AlertKey key, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _pending.Forget(key);
        return CloseAsync(key, recoveryIncident: null, reason, ct);
    }

    public async Task<IReadOnlyList<string>> RetireUnconfiguredAsync(
        string keyPrefix, IEnumerable<AlertKey> configured, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var keep = configured.Select(k => k.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A breach still waiting for something that is no longer watched would wait forever.
        foreach (var waiting in _pending.Keys())
        {
            if (waiting.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && !keep.Contains(waiting))
                _pending.Forget(new AlertKey(waiting));
        }

        List<string> retire;
        lock (_gate)
        {
            retire = States().Values
                .Where(s => s.IsOpen && s.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && !keep.Contains(s.Key))
                .Select(s => s.Key)
                .ToList();
        }

        var retired = new List<string>();
        foreach (var key in retire)
        {
            if (await CloseAsync(new AlertKey(key), recoveryIncident: null, reason, ct))
                retired.Add(key);
        }
        return retired;
    }

    /// <summary>
    /// Closes an open alert: resolved (the condition cleared) or, with
    /// <paramref name="retiredReason"/>, retired (what it watched is no longer monitored).
    /// </summary>
    private async Task<bool> CloseAsync(AlertKey key, Incident? recoveryIncident, string? retiredReason, CancellationToken ct)
    {
        using var keyLock = await LockKeyAsync(key, ct);

        var now = UtcNow;
        var options = _options.CurrentValue;
        var inMaintenance = _maintenance.Current.Active;

        AlertState existing;
        long occurrence;
        TimeSpan duration;
        string description;
        PendingDelivery? recovery = null;

        // One section from reading the alert to replacing its notice: a delivery cannot claim the
        // opened notice, or count it as notified, in between.
        lock (_gate)
        {
            if (States().GetValueOrDefault(key.Value) is not { IsOpen: true } open)
                return false;

            existing = open;
            occurrence = existing.Occurrence;
            duration = now - existing.OpenedAt;
            description = retiredReason is not null
                ? $"No longer monitored ({retiredReason}); closed after {AlertText.FormatDuration(duration)}. " +
                  $"Last state: {existing.LastDescription}"
                : recoveryIncident?.Description
                  // No dedicated recovery incident type (CPU, disks, queues).
                  ?? $"Resolved after {AlertText.FormatDuration(duration)}: {existing.LastDescription}";

            Save(existing with { ResolvedAt = now, RetiredReason = retiredReason });

            // Only this occurrence's notices are replaced: an earlier occurrence's recovery, still
            // queued, is never touched. An opened notice that failed on every channel so far still
            // tells its story as a resolved notice, and so does one being sent right now (it may
            // reach operators any moment; it is superseded below, so it cannot count as notified
            // afterwards). One that was never attempted on any channel is dropped silently.
            var pending = PendingNoticeLocked(key.Value, occurrence);
            var attempted = (pending is { IsAboutOpenAlert: true } &&
                             (pending.FailedAttempts > 0 || pending.DeliveredChannels.Count > 0)) ||
                            (_sending.GetValueOrDefault(key.Value) is { IsAboutOpenAlert: true } sending &&
                             sending.Occurrence == occurrence);

            // An opening that could not go out because its channels keep failing (it waits behind
            // an earlier notice that failed on them, or for their circuit) would have been attempted,
            // and have failed, if those channels took attempts: it still tells its story as a
            // resolved notice once they work again (folded into a flapping notice if the alert
            // keeps flapping). One merely queued behind a send in progress goes silently.
            var waitedOnFailures = !attempted && pending is { IsAboutOpenAlert: true, Held: false } &&
                                   WaitsOnFailingChannelsLocked(pending);

            var notification = new AlertNotification(
                AlertNotificationKind.Resolved,
                key.Value,
                existing.Severity,
                existing.Title,
                description,
                _server,
                now,
                OpenedAtUtc: existing.OpenedAt,
                Duration: duration,
                Incident: recoveryIncident,
                NotifyCount: existing.NotifyCount,
                RetiredReason: retiredReason);

            if (options.SendRecovery && (existing.NotifyCount > 0 || attempted))
            {
                // Operators know about the alert: tell them it cleared (once the window ends, if held).
                // Explicit: never folded into a maintenance summary, even if no channel has counted
                // the opening yet (it may still reach operators once its send completes).
                recovery = EnqueueLocked(notification, occurrence, now, held: inMaintenance, explicitRecovery: true);
            }
            else if (options.SendRecovery && (inMaintenance || pending is { Held: true }))
            {
                // Opened and cleared while notifications were held: kept for the end-of-window summary.
                recovery = EnqueueLocked(notification, occurrence, now, held: true);
            }
            else if (options.SendRecovery && waitedOnFailures)
            {
                recovery = EnqueueLocked(notification, occurrence, now, held: false);
            }
            else
            {
                RemoveNoticesLocked(key.Value, occurrence);
            }
        }

        // Nothing is claimed yet: whatever fails here (the portal, the metrics store) leaves the
        // recovery queued for the delivery worker.
        if (retiredReason is not null || recoveryIncident is null)
        {
            // Retired, or no dedicated recovery incident: tell the portal the alert of this type
            // cleared. Not recorded locally, where it would read as a new breach.
            await _portal.PublishAlertEventAsync(
                new Incident(now, existing.IncidentType, description), key.Value, resolved: true, ct);
        }
        else
        {
            _metricsStore.AddIncident(recoveryIncident);
            await _portal.PublishAlertEventAsync(recoveryIncident, key.Value, resolved: true, ct);
        }

        if (retiredReason is not null)
            _logger.LogInformation("Alert {Key} retired after {Duration}: {Reason}", key.Value, AlertText.FormatDuration(duration), retiredReason);
        else
            _logger.LogInformation("Alert {Key} resolved after {Duration}", key.Value, AlertText.FormatDuration(duration));

        if (recovery is not null)
            await ScheduleAsync(recovery, ct);
        return true;
    }

    public async Task NotifyEventAsync(
        AlertKey key, AlertSeverity severity, string title, Incident incident, CancellationToken ct = default)
    {
        _metricsStore.AddIncident(incident);
        await _portal.PublishAlertEventAsync(incident, key.Value, resolved: null, ct);
        _logger.LogInformation("Event {Key}: {Description}", key.Value, incident.Description);

        // During maintenance: recorded and published now, listed in the end-of-window summary.
        await DeliverAsync(new AlertNotification(
            AlertNotificationKind.Event, key.Value, severity, title, incident.Description, _server,
            incident.Timestamp, Incident: incident), occurrence: 0, UtcNow, held: _maintenance.Current.Active, ct);
    }

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        if (_maintenance.Current.Active)
            return;
        if (!await _processGate.WaitAsync(0, ct))
            return; // a run is already in progress

        try
        {
            var now = UtcNow;
            lock (_gate)
            {
                SummariseHeldLocked(now);
                ExpireLocked(now);
            }

            // Everything due, in the scheduler's order (see ClaimNextLocked). A window that starts
            // while the backlog is drained holds the rest of it (checked again right before each
            // send, see PrepareLocked).
            await PumpAsync(trigger: null, ct);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private static bool IsReminderDue(AlertState state, DateTime now, AlertingOptions options)
    {
        // Never notified and nothing pending (no channel was configured yet): offer it again.
        if (state.LastNotifiedAt is not { } last)
            return true;
        return options.RenotifyMinutes > 0 && now - last >= TimeSpan.FromMinutes(options.RenotifyMinutes);
    }

    // ── Delivery ───────────────────────────────────────────────────────────

    /// <summary>
    /// Persists <paramref name="notification"/> as a pending delivery (an alert notice replaces the
    /// previous notice of the same <paramref name="occurrence"/>) and, unless
    /// <paramref name="held"/>, schedules it (see <see cref="ScheduleAsync"/>).
    /// </summary>
    private async Task DeliverAsync(
        AlertNotification notification, long occurrence, DateTime now, bool held, CancellationToken ct)
    {
        PendingDelivery? delivery;
        lock (_gate)
            delivery = EnqueueLocked(notification, occurrence, now, held);
        if (delivery is not null)
            await ScheduleAsync(delivery, ct);
    }

    /// <summary>
    /// Persists a new delivery with its channel obligations (<see cref="PendingDelivery.Channels"/>:
    /// the channels configured right now that take its severity, fixed from here on). Nothing is
    /// claimed here: every send goes through the scheduler (<see cref="ClaimNextLocked"/>), so a
    /// new notice never takes a channel's attempt ahead of older or more urgent work. An alert
    /// notice goes to the end of its key's queue: it replaces the pending notice of the same
    /// <paramref name="occurrence"/> (a reminder, an escalation or the recovery superseding the
    /// opened notice) but never an earlier occurrence's, and the queue is kept within
    /// <see cref="MaxQueuedNotices"/>.
    /// <para>
    /// Null when no channel takes it (none configured, or none for its severity): nothing is
    /// queued, and an open alert stays un-notified, so its next raise offers it again (to the
    /// channels configured by then). A new critical opening or recovery may let a backing-off
    /// channel be probed early (<see cref="ChannelRateLimiter.NoteCritical"/>).
    /// </para>
    /// </summary>
    private PendingDelivery? EnqueueLocked(
        AlertNotification notification, long occurrence, DateTime now, bool held, bool explicitRecovery = false)
    {
        var isNotice = notification.Kind is AlertNotificationKind.Opened or AlertNotificationKind.Reminder
            or AlertNotificationKind.Resolved;
        if (isNotice)
            RemoveNoticesLocked(notification.AlertKey, occurrence);

        var channels = DistinctChannels(_dispatcher.ChannelsFor(notification) ?? []);
        if (channels.Count == 0)
        {
            _logger.LogDebug("No notification channel takes the {Severity} {Kind} notification for {Key}",
                notification.Severity, notification.Kind, notification.AlertKey);
            return null;
        }

        // Before the new notice takes its sequence number, so the flapping notice sorts
        // before it in creation order too.
        if (isNotice)
            CoalesceLocked(notification.AlertKey, now);

        var delivery = PendingDelivery.Create(notification, now, held, explicitRecovery,
            isNotice ? occurrence : 0, NextSequenceLocked()) with
        {
            Channels = channels,
            EnqueuedAt = _time.GetTimestamp(),
        };
        _store.UpsertDelivery(delivery);
        Deliveries()[delivery.Id] = delivery;
        if (!held && IsCriticalTrigger(delivery))
        {
            foreach (var channel in channels)
                Limiter().NoteCritical(channel);
        }
        return delivery;
    }

    /// <summary>A new critical opening (an escalation too) or the recovery of a critical alert.</summary>
    private static bool IsCriticalTrigger(PendingDelivery delivery) =>
        delivery is { Notification.Severity: AlertSeverity.Critical, IsFlapping: false } &&
        delivery.Notification.Kind is AlertNotificationKind.Opened or AlertNotificationKind.Resolved;

    private static List<string> DistinctChannels(IEnumerable<string> channels) =>
        channels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Sends what is due now, in the scheduler's order (see <see cref="PumpAsync"/>), after
    /// <paramref name="delivery"/> was queued, or logs that it is held by a maintenance window.
    /// </summary>
    private async Task ScheduleAsync(PendingDelivery delivery, CancellationToken ct)
    {
        if (delivery.Held)
        {
            _logger.LogInformation("{Kind} notification for alert {Key} held: maintenance window active until {Until:O}",
                delivery.Notification.Kind, delivery.AlertKey, _maintenance.Current.Until);
            return;
        }
        await PumpAsync(delivery, ct);
    }

    // ── Scheduler ──────────────────────────────────────────────────────────

    /// <summary>
    /// Claims the next dispatch, for every caller alike (a raise, a close, an event or the
    /// delivery worker): one notice and the channels it goes to now, each with its
    /// <see cref="ChannelRateLimiter.Lease"/>. Null when nothing can be sent now.
    /// <para>
    /// A (notice, channel) pair is eligible when the channel is still owed the notice
    /// (<see cref="PendingDelivery.IsPendingOn"/>), the notice can be claimed (not in flight, no
    /// other notice of its alert in flight, not expired, not held for the end-of-window summary),
    /// the channel's own retry deadline for it has passed, the caller has not attempted the pair in
    /// this pass, and the per-channel ordering barrier lets it through: every notice queued ahead
    /// of it in its alert's queue (<see cref="PendingDelivery.QueueOrder"/>) has reached the
    /// channel, was skipped there or was given up. Each channel thus gets an alert's notices in
    /// order (also across a restart: obligations and outcomes are persisted) and advances through
    /// the queue on its own, so one that keeps failing never holds back the channels that work.
    /// </para>
    /// <para>
    /// Fairness: among the eligible pairs of every channel that may take an attempt now
    /// (<see cref="ChannelRateLimiter.CanAcquire"/>), each channel's next notice is the one with
    /// the highest priority, then the oldest (lowest <see cref="PendingDelivery.Sequence"/>). A
    /// notice's priority is its severity (a recovery carries the severity of the alert it
    /// resolves), raised to that of any more severe notice of its alert waiting behind it on the
    /// channel, plus one level per <see cref="AgingInterval"/> waited, up to critical. The
    /// best-ranked of these is claimed, on every channel whose next notice it is.
    /// </para>
    /// </summary>
    private Claim? ClaimNextLocked(IReadOnlySet<(string, string)> attempted)
    {
        var now = UtcNow;
        SkipUnconfiguredLocked();

        Dictionary<string, (PendingDelivery Notice, int Priority)>? next = null; // per channel
        var admissible = new Dictionary<string, (bool Ok, bool EarlyProbe)>(StringComparer.OrdinalIgnoreCase);
        (bool Ok, bool EarlyProbe) Admission(string channel)
        {
            if (!admissible.TryGetValue(channel, out var admission))
            {
                var ok = Limiter().CanAcquire(channel, out var earlyProbe);
                admissible[channel] = admission = (ok, earlyProbe);
            }
            return admission;
        }

        foreach (var queue in QueuesLocked())
        {
            HashSet<string>? blocked = null; // channels a notice ahead still has to reach
            for (var i = 0; i < queue.Count; i++)
            {
                var notice = queue[i];
                var pending = notice.PendingChannels.ToList();
                if (IsClaimableLocked(notice, now))
                {
                    foreach (var channel in pending)
                    {
                        if (blocked?.Contains(channel) == true || attempted.Contains(AttemptOf(notice.Id, channel)))
                            continue;
                        // A critical early probe tests the channel: the notice it carries goes even
                        // before its own retry deadline (the probe's cooldown bounds these).
                        var (ok, earlyProbe) = Admission(channel);
                        if (!ok || (!earlyProbe && !notice.IsDueOn(channel, _time)))
                            continue;
                        var priority = PriorityLocked(queue, i, channel);
                        next ??= new Dictionary<string, (PendingDelivery, int)>(StringComparer.OrdinalIgnoreCase);
                        if (!next.TryGetValue(channel, out var current) ||
                            Outranks(notice, priority, current.Notice, current.Priority))
                            next[channel] = (notice, priority);
                    }
                }
                if (notice.IsExpired(now))
                    continue; // given up: it no longer holds back the notices behind it
                foreach (var channel in pending)
                    (blocked ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(channel);
            }
        }
        if (next is null)
            return null;

        var top = next.Values.Aggregate((a, b) => Outranks(b.Notice, b.Priority, a.Notice, a.Priority) ? b : a).Notice;
        List<ChannelRateLimiter.Lease>? leases = null;
        foreach (var (channel, candidate) in next)
        {
            if (candidate.Notice.Id == top.Id && Limiter().TryAcquire(channel) is { } lease)
                (leases ??= []).Add(lease);
        }
        if (leases is null)
            return null;
        ClaimLocked(top);
        return new Claim(top, leases);
    }

    /// <summary>Ranking of the scheduler: higher priority first, then the older notice.</summary>
    private static bool Outranks(PendingDelivery a, int priorityA, PendingDelivery b, int priorityB) =>
        priorityA > priorityB || (priorityA == priorityB && a.Sequence < b.Sequence);

    /// <summary>
    /// The priority of <paramref name="queue"/>[<paramref name="index"/>] on
    /// <paramref name="channel"/> (see <see cref="ClaimNextLocked"/>): its severity, or that of a
    /// more severe notice of the alert queued behind it for the channel (which cannot go before
    /// it), plus its aging.
    /// </summary>
    private int PriorityLocked(List<PendingDelivery> queue, int index, string channel)
    {
        var notice = queue[index];
        var severity = notice.Notification.Severity;
        for (var j = index + 1; j < queue.Count; j++)
        {
            if (queue[j].Notification.Severity > severity && queue[j].IsPendingOn(channel))
                severity = queue[j].Notification.Severity;
        }

        var waited = _time.GetElapsedTime(notice.EnqueuedAt);
        var boost = waited <= TimeSpan.Zero ? 0 : (int)Math.Min((int)AlertSeverity.Critical, waited / AgingInterval);
        return Math.Min((int)AlertSeverity.Critical, (int)severity + boost);
    }

    /// <summary>Each alert key's queue (in queue order), and every other delivery on its own.</summary>
    private List<List<PendingDelivery>> QueuesLocked()
    {
        var queues = new List<List<PendingDelivery>>();
        foreach (var key in Deliveries().Values.Where(d => d.IsAlertNotice).GroupBy(d => d.AlertKey, StringComparer.OrdinalIgnoreCase))
            queues.Add(key.OrderBy(d => d.QueueOrder).ToList());
        foreach (var other in Deliveries().Values.Where(d => !d.IsAlertNotice))
            queues.Add([other]);
        return queues;
    }

    /// <summary>
    /// Whether <paramref name="delivery"/> may be claimed now (see <see cref="ClaimNextLocked"/>).
    /// A held notice (its window has ended, the caller checked) is released when it is prepared
    /// (<see cref="PrepareLocked"/>), except the ones the end-of-window summary lists instead
    /// (<see cref="SummariseHeldLocked"/>, run by the delivery worker).
    /// </summary>
    private bool IsClaimableLocked(PendingDelivery delivery, DateTime now) =>
        !delivery.IsExpired(now) && CanClaimLocked(delivery) &&
        !(delivery.Held && (delivery.Notification.Kind == AlertNotificationKind.Event || delivery.IsSummarisedRecovery));

    /// <summary>
    /// Marks as skipped the obligations of channels that no longer take a notice (removed from the
    /// configuration, unavailable, or their <c>MinSeverity</c> now excludes it), so they stop
    /// holding back the alert's later notices; recorded, so restoring the configuration never
    /// brings them back. A notice no channel is owed any more is settled (removed), unless it is
    /// held. Notices in flight are left to their sender.
    /// </summary>
    private void SkipUnconfiguredLocked()
    {
        var deliveries = Deliveries();
        foreach (var d in deliveries.Values.ToList())
        {
            if (_inFlight.Contains(d.Id))
                continue;
            var pending = d.PendingChannels.ToList();
            if (pending.Count > 0)
            {
                var targets = TargetsLocked(d);
                var skipped = pending.Where(c => !targets.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (skipped.Count == 0)
                    continue;
                _logger.LogWarning("{Kind} notification for {Key} is no longer sent to {Channels}: removed from the configuration " +
                                   "or below their minimum severity", d.Notification.Kind, d.AlertKey, string.Join(", ", skipped));
                var updated = d with
                {
                    SkippedChannels = [.. d.SkippedChannels, .. skipped],
                    ChannelRetries = d.ChannelRetries
                        .Where(r => !skipped.Contains(r.Channel, StringComparer.OrdinalIgnoreCase))
                        .ToList(),
                };
                if (updated.Held || updated.PendingChannels.Any())
                {
                    _store.UpsertDelivery(updated);
                    deliveries[d.Id] = updated;
                    continue;
                }
            }
            else if (d.Held)
            {
                continue;
            }

            _store.DeleteDelivery(d.Id);
            deliveries.Remove(d.Id);
        }
    }

    /// <summary>A (delivery, channel) attempt, as recorded by a sender's pass.</summary>
    private static (string, string) AttemptOf(string id, string channel) => (id, channel.ToUpperInvariant());

    /// <summary>
    /// The channels that take <paramref name="delivery"/> under the current configuration (see
    /// <see cref="INotificationDispatcher.ChannelsFor"/>). Never adds an obligation: only used to
    /// skip the ones the configuration no longer allows.
    /// </summary>
    private IReadOnlyList<string> TargetsLocked(PendingDelivery delivery) =>
        _dispatcher.ChannelsFor(delivery.Notification) ?? [];

    /// <summary>
    /// Whether <paramref name="pending"/> (an opened notice not attempted yet) is held back by
    /// failing channels: a channel it still has to reach is failing (its circuit is open or
    /// probing), or a notice ahead of it in the alert's queue failed on that channel.
    /// </summary>
    private bool WaitsOnFailingChannelsLocked(PendingDelivery pending)
    {
        var ahead = KeyQueueLocked(pending.AlertKey).TakeWhile(d => d.Id != pending.Id).ToList();
        return pending.PendingChannels.Any(c =>
            Limiter().IsFailing(c) || ahead.Any(d => d.IsPendingOn(c) && d.RetryOf(c) is not null));
    }

    private long NextSequenceLocked()
    {
        Deliveries(); // loading them sets the last sequence used
        return ++_lastSequence;
    }

    /// <summary>
    /// Makes room for one more notice in <paramref name="key"/>'s queue (see
    /// <see cref="MaxQueuedNotices"/>): keeps its oldest <see cref="KeptOldestNotices"/> notices
    /// (up to an earlier flapping notice) and folds every later one into a single new flapping
    /// notice. Only earlier occurrences are folded: the caller has already removed the notice of
    /// the occurrence it is about to queue, which becomes the newest state behind it.
    /// <para>
    /// A folded notice may be in flight, or have reached some channels: its row goes (like a
    /// superseded one) and the flapping notice covers it, in its place in the queue, on every
    /// channel still owed at least one folded notice. Its obligations are the folded notices'
    /// channels: one that already has every folded notice it was owed counts as having the
    /// flapping notice too, one that was skipped for all of them is skipped. The flapping notice
    /// is explicit (sent on its own) if any folded notice was; one standing only for occurrences
    /// operators were never told about stays eligible for the maintenance summary.
    /// </para>
    /// </summary>
    private void CoalesceLocked(string key, DateTime now)
    {
        var queue = KeyQueueLocked(key);
        if (queue.Count < MaxQueuedNotices)
            return;

        var kept = queue.Take(KeptOldestNotices).TakeWhile(d => !d.IsFlapping).Count();
        var folded = queue.Skip(kept).ToList();
        var count = folded.Sum(d => Math.Max(1, d.FlappingCount));
        var from = folded.Min(d => d.Notification.OpenedAtUtc ?? d.Notification.OccurredAtUtc);
        var to = folded.Max(d => d.Notification.OccurredAtUtc);
        var alert = folded.Last(d => !d.IsFlapping).Notification;
        var stamp = (DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        var channels = DistinctChannels(folded.SelectMany(d => d.Channels ?? []));

        var notification = new AlertNotification(
            AlertNotificationKind.Resolved,
            key,
            folded.Max(d => d.Notification.Severity),
            $"Flapping: {count} occurrences between {stamp(from)} and {stamp(to)}",
            $"{alert.Title} opened and cleared {count} times between {stamp(from)} and {stamp(to)}. " +
            "Their notices were folded into this one; earlier and later occurrences are notified on their own.",
            _server,
            to,
            OpenedAtUtc: from,
            Duration: to - from,
            NotifyCount: folded.Max(d => d.Notification.NotifyCount));
        var flapping = PendingDelivery.Create(
            notification, now,
            held: folded.Any(d => d.Held),
            explicitRecovery: folded.Any(d => !d.IsSummarisedRecovery),
            occurrence: folded.Max(d => d.Occurrence),
            sequence: NextSequenceLocked()) with
        {
            FlappingCount = count,
            Channels = channels,
            DeliveredChannels = channels.Where(c => !folded.Any(d => d.IsPendingOn(c)) && folded.Any(d => d.HasDelivered(c))).ToList(),
            SkippedChannels = channels.Where(c => !folded.Any(d => d.IsPendingOn(c) || d.HasDelivered(c))).ToList(),
            // It stands for the oldest folded notice's wait.
            EnqueuedAt = folded.Min(d => d.EnqueuedAt),
        };

        // Written before the folded rows go: a crash in between repeats a notice, never loses one.
        _store.UpsertDelivery(flapping);
        Deliveries()[flapping.Id] = flapping;
        foreach (var old in folded)
        {
            _store.DeleteDelivery(old.Id);
            Deliveries().Remove(old.Id);
        }
        _logger.LogWarning("Alert {Key} is flapping: {Count} occurrences between {From:O} and {To:O} folded into one notice",
            key, count, from, to);
    }

    /// <summary>
    /// Whether <paramref name="delivery"/> may be claimed: it is not claimed already and (alert
    /// notices) no other notice of the same alert is being sent (see <see cref="_sending"/>).
    /// Which notice goes next, and where, is decided by <see cref="ClaimNextLocked"/>.
    /// </summary>
    private bool CanClaimLocked(PendingDelivery delivery) =>
        !_inFlight.Contains(delivery.Id) && !(delivery.IsAlertNotice && _sending.ContainsKey(delivery.AlertKey));

    private void ClaimLocked(PendingDelivery delivery)
    {
        _inFlight.Add(delivery.Id);
        if (delivery.IsAlertNotice)
            _sending[delivery.AlertKey] = delivery;
    }

    /// <summary>
    /// Gives back a claim, and the leases of its channels that have no outcome (the attempt never
    /// started, was cut short by a shutdown, or the dispatcher reported nothing for it).
    /// </summary>
    private void ReleaseClaimLocked(Claim claimed)
    {
        var delivery = claimed.Delivery;
        _inFlight.Remove(delivery.Id);
        if (_sending.GetValueOrDefault(delivery.AlertKey) is { } sending && sending.Id == delivery.Id)
            _sending.Remove(delivery.AlertKey);
        foreach (var lease in claimed.Leases)
            CompleteLeaseLocked(lease, success: null);
    }

    /// <summary>Records a leased channel's outcome with its rate limiter (persisting a changed circuit).</summary>
    private void CompleteLeaseLocked(ChannelRateLimiter.Lease lease, bool? success)
    {
        if (Limiter().Complete(lease, success))
            PersistCircuitsLocked();
    }

    /// <summary>Records <paramref name="channel"/>'s outcome for <paramref name="claimed"/> with its rate limiter.</summary>
    private void RecordOutcomeLocked(Claim claimed, string channel, bool success)
    {
        var lease = claimed.Leases.FirstOrDefault(l => string.Equals(l.Channel, channel, StringComparison.OrdinalIgnoreCase));
        if (lease is not null)
            CompleteLeaseLocked(lease, success);
        else if (Limiter().RecordUnleased(channel, success))
            PersistCircuitsLocked();
    }

    /// <summary>
    /// Sends everything that may go out now, one dispatch at a time, in the scheduler's order
    /// (<see cref="ClaimNextLocked"/>): the one admission path of every send, so a raise, a close
    /// or an event that triggers it never takes a channel's attempt ahead of waiting work that
    /// ranks higher; it may send that work itself. Several callers may pump at once: each claim
    /// is exclusive, and whoever finishes a dispatch schedules again, so a notice another caller
    /// left behind (its alert or channel was busy) is never stranded.
    /// <para>
    /// Each (notice, channel) is attempted at most once per pump: one that failed waits for its
    /// retry deadline or its circuit. A maintenance window that starts stops the pump (what was not
    /// attempted yet is held until the window ends, see <see cref="HoldQueuedLocked"/>), and so
    /// does a shutdown: a dispatch cut short by <paramref name="ct"/> leaves the rest pending for
    /// the delivery worker or the next start. <paramref name="trigger"/> is the notice the caller
    /// just queued, if any (only logged).
    /// </para>
    /// </summary>
    private async Task PumpAsync(PendingDelivery? trigger, CancellationToken ct)
    {
        var attempted = new HashSet<(string, string)>();
        for (var first = true; ; first = false)
        {
            ct.ThrowIfCancellationRequested();
            Claim? claimed;
            lock (_gate)
            {
                if (_maintenance.Current.Active)
                {
                    HoldQueuedLocked();
                    return;
                }
                claimed = ClaimNextLocked(attempted);
            }

            if (claimed is null)
            {
                if (first && trigger is not null)
                {
                    _logger.LogDebug("{Kind} notification for alert {Key} waits for its channels (an earlier notice, " +
                                     "a retry, the rate limit or more urgent notices)", trigger.Notification.Kind, trigger.AlertKey);
                }
                return;
            }

            foreach (var lease in claimed.Leases)
                attempted.Add(AttemptOf(claimed.Delivery.Id, lease.Channel));
            try
            {
                await SendClaimedAsync(claimed, ct);
            }
            finally
            {
                lock (_gate)
                    ReleaseClaimLocked(claimed);
            }
        }
    }

    /// <summary>
    /// A maintenance window started while deliveries were queued: those no channel was attempted
    /// for yet are held, as if they had been queued during the window (released, brought up to
    /// date, or listed in the end-of-window summary when it ends).
    /// </summary>
    private void HoldQueuedLocked()
    {
        foreach (var current in Deliveries().Values.ToList())
        {
            if (current.Held || _inFlight.Contains(current.Id) || current.FailedAttempts > 0 ||
                current.DeliveredChannels.Count > 0)
                continue;
            var held = current with { Held = true };
            _store.UpsertDelivery(held);
            Deliveries()[held.Id] = held;
            _logger.LogInformation("{Kind} notification for alert {Key} held: maintenance window active until {Until:O}",
                held.Notification.Kind, held.AlertKey, _maintenance.Current.Until);
        }
    }

    /// <summary>Most held items listed one by one in the end-of-window summary.</summary>
    internal const int MaxSummaryLines = 20;

    /// <summary>
    /// A maintenance window ended. Held notices operators should get one by one stay as they are
    /// (alerts still open, and resolutions of alerts whose opening may have reached operators); they
    /// are released when the scheduler sends them (<see cref="PrepareLocked"/>). Everything else
    /// that was held — alerts that
    /// opened and cleared inside the window, one-shot events such as the patch reboot — is
    /// replaced by a single summary notification.
    /// </summary>
    private void SummariseHeldLocked(DateTime now)
    {
        var deliveries = Deliveries();
        var held = deliveries.Values
            .Where(d => d.Held && (d.Notification.Kind == AlertNotificationKind.Event || d.IsSummarisedRecovery))
            .OrderBy(d => d.Notification.OccurredAtUtc)
            .ToList();
        if (held.Count == 0)
            return;

        // Same held rows, same id: a crash before they were removed cannot produce a second summary.
        var id = "maintenance-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(",", held.Select(d => d.Id).Order(StringComparer.Ordinal)))))[..32];
        if (!deliveries.ContainsKey(id))
        {
            var lines = held.Take(MaxSummaryLines).Select(SummaryLine).ToList();
            if (held.Count > MaxSummaryLines)
                lines.Add($"… and {held.Count - MaxSummaryLines} more (see the incident log)");
            var summary = new AlertNotification(
                AlertNotificationKind.Event,
                AlertKey.Maintenance.Value,
                held.Max(d => d.Notification.Severity),
                $"Maintenance ended: {held.Count} held notification{(held.Count == 1 ? "" : "s")}",
                "Held back during the maintenance window:\n" + string.Join("\n", lines),
                _server,
                now);
            var channels = DistinctChannels(_dispatcher.ChannelsFor(summary) ?? []);
            if (channels.Count > 0)
            {
                var delivery = PendingDelivery.Create(summary, now, held: false, sequence: NextSequenceLocked()) with
                {
                    Id = id,
                    Channels = channels,
                    EnqueuedAt = _time.GetTimestamp(),
                };
                _store.UpsertDelivery(delivery);
                deliveries[id] = delivery;
            }
        }

        foreach (var d in held)
        {
            _store.DeleteDelivery(d.Id);
            deliveries.Remove(d.Id);
        }
        _logger.LogInformation("Maintenance window ended: {Count} held notifications summarised", held.Count);
    }

    private static string SummaryLine(PendingDelivery d)
    {
        var n = d.Notification;
        var at = DateTime.SpecifyKind(n.OccurredAtUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        var label = AlertText.Label(n.Severity);
        if (d.IsFlapping)
            return $"• {at} [{label}] {n.Title}: {n.Description}";
        return n switch
        {
            { Kind: AlertNotificationKind.Resolved, RetiredReason: { } reason } =>
                $"• {at} [{label}] {n.Title}: no longer monitored ({reason})",
            { Kind: AlertNotificationKind.Resolved } =>
                $"• {at} [{label}] {n.Title}: opened and cleared after {AlertText.FormatDuration(n.Duration ?? TimeSpan.Zero)}",
            _ => $"• {at} [{label}] {n.Title}: {n.Description}",
        };
    }

    /// <summary>
    /// A maintenance window ended: an opened/reminder notice is brought up to date (still open
    /// for how long, latest description) and sent; one whose occurrence is over is dropped.
    /// </summary>
    private PendingDelivery? ReleaseLocked(PendingDelivery delivery, DateTime now)
    {
        var released = delivery with { Held = false, NextAttemptAt = now };
        if (delivery.IsAboutOpenAlert)
        {
            var state = States().GetValueOrDefault(delivery.AlertKey);
            if (state is not { IsOpen: true } || state.Occurrence != delivery.Occurrence)
            {
                _store.DeleteDelivery(delivery.Id);
                Deliveries().Remove(delivery.Id);
                return null;
            }

            released = released with
            {
                Notification = delivery.Notification with
                {
                    Kind = state.NotifyCount == 0 ? AlertNotificationKind.Opened : AlertNotificationKind.Reminder,
                    Severity = state.Severity,
                    Title = state.Title,
                    Description = state.LastDescription,
                    OccurredAtUtc = now,
                    Duration = now - state.OpenedAt,
                    NotifyCount = state.NotifyCount + 1,
                },
            };
        }

        _store.UpsertDelivery(released);
        Deliveries()[released.Id] = released;
        return released;
    }

    /// <summary>
    /// Sends the claimed delivery to the channels it was claimed for and records the outcome per
    /// channel: delivered channels are never sent it again, failed ones are retried with
    /// <see cref="NotificationRetryPolicy"/> (each on its own deadline). The caller
    /// (<see cref="PumpAsync"/>) holds the in-flight claim and the channels' leases and releases
    /// them on every path, including a failure while the send is prepared (releasing a held
    /// delivery) or its outcome is persisted.
    /// </summary>
    private async Task SendClaimedAsync(Claim claimed, CancellationToken ct)
    {
        PendingDelivery? prepared;
        lock (_gate)
            prepared = PrepareLocked(claimed.Delivery);
        if (prepared is null)
            return;

        PendingDelivery delivery = prepared;
        var notification = delivery.Notification;
        var channels = claimed.Leases.Select(l => l.Channel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reached = delivery.DeliveredChannels.Count > 0; // guarded by _gate
        var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // guarded by _gate

        // Each channel is recorded as soon as it has its outcome: a delivery so that a dispatch cut
        // short (e.g. by a graceful shutdown waiting on a slow channel) never loses, or later
        // repeats, it; a failure so that the channel's circuit holds back other alerts at once.
        void OnChannelCompleted(ChannelDelivery channel)
        {
            try
            {
                lock (_gate)
                {
                    RecordOutcomeLocked(claimed, channel.Channel, channel.Success);
                    if (channel.Success)
                    {
                        delivered.Add(channel.Channel);
                        RecordDeliveredLocked(delivery, channel.Channel, ref reached);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not record the outcome of the {Kind} notification for alert {Key} on {Channel}",
                    notification.Kind, notification.AlertKey, channel.Channel);
            }
        }

        DispatchResult result;
        string? error = null;
        try
        {
            result = await _dispatcher.DispatchAsync(notification, channels, OnChannelCompleted, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Dispatching the {Kind} notification for alert {Key} failed",
                notification.Kind, notification.AlertKey);
            result = DispatchResult.None;
            error = $"Dispatch failed ({ex.GetType().Name})";
        }

        var now = UtcNow;
        lock (_gate)
        {
            // Channels a dispatcher reported only in its result (not as they completed).
            foreach (var channel in result.Delivered)
            {
                delivered.Add(channel);
                RecordDeliveredLocked(delivery, channel, ref reached);
            }

            // What failed, per channel: a dispatch that threw failed every channel it had not
            // delivered yet.
            var failed = result.Failed
                .Where(f => !delivered.Contains(f.Channel))
                .Select(f => (f.Channel, f.Message))
                .ToList();
            if (error is { } message)
                failed.AddRange(channels.Where(c => !delivered.Contains(c)).Select(c => (Channel: c, Message: message)));
            foreach (var lease in claimed.Leases)
            {
                CompleteLeaseLocked(lease, delivered.Contains(lease.Channel) ? true
                    : failed.Any(f => string.Equals(f.Channel, lease.Channel, StringComparison.OrdinalIgnoreCase)) ? false
                    : null);
            }

            // Superseded while it was being sent: the newer notice of its occurrence, the close or
            // the flapping notice it was folded into owns the row.
            if (Deliveries().GetValueOrDefault(delivery.Id) is not { } current)
                return;
            var next = NextAttempt(current, failed, now);
            if (next is null)
            {
                _store.DeleteDelivery(delivery.Id);
                Deliveries().Remove(delivery.Id);
            }
            else
            {
                _store.UpsertDelivery(next);
                Deliveries()[delivery.Id] = next;
            }
        }
    }

    /// <summary>
    /// The claimed delivery as it is about to be sent: its current row (null if it was superseded
    /// meanwhile), released first if a maintenance window held it (null if it is no longer worth
    /// sending, see <see cref="ReleaseLocked"/>).
    /// <para>
    /// The one place every send passes (opened, reminder, recovery and event notices, sent
    /// directly or by the delivery worker) right before the dispatcher, so maintenance is checked
    /// here: callers decide earlier, sometimes before an await (the portal publication), and a
    /// window may have started since. The notice is then held (null) until the window ends.
    /// </para>
    /// </summary>
    private PendingDelivery? PrepareLocked(PendingDelivery claimed)
    {
        if (Deliveries().GetValueOrDefault(claimed.Id) is not { } current)
            return null;

        if (_maintenance.Current is { Active: true } window)
        {
            if (!current.Held)
            {
                var held = current with { Held = true };
                _store.UpsertDelivery(held);
                Deliveries()[held.Id] = held;
                _logger.LogInformation("{Kind} notification for alert {Key} held: maintenance window active until {Until:O}",
                    held.Notification.Kind, held.AlertKey, window.Until);
            }
            return null;
        }

        return current.Held ? ReleaseLocked(current, UtcNow) : current;
    }

    /// <summary>
    /// Records that <paramref name="channel"/> has <paramref name="delivery"/> (persisted at once)
    /// and, the first time an opened or reminder notice reaches any channel, counts the alert as
    /// notified. A superseded delivery has no row left to update, but still counts.
    /// </summary>
    private void RecordDeliveredLocked(PendingDelivery delivery, string channel, ref bool reached)
    {
        // Counted as notified once it actually reached a channel, not when it was attempted.
        if (!reached && delivery.IsAboutOpenAlert)
            MarkNotified(delivery, UtcNow);
        reached = true;

        if (Deliveries().GetValueOrDefault(delivery.Id) is not { } current || current.HasDelivered(channel))
            return;
        var updated = current with
        {
            DeliveredChannels = [.. current.DeliveredChannels, channel],
            ChannelRetries = current.ChannelRetries
                .Where(r => !string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
        _store.UpsertDelivery(updated);
        Deliveries()[delivery.Id] = updated;
    }

    /// <summary>
    /// What is left of <paramref name="current"/> (its delivered channels already recorded) after
    /// an attempt in which the <paramref name="failed"/> channels failed: each of them is retried
    /// on its own deadline (<see cref="NotificationRetryPolicy.Backoff"/> of its own failures,
    /// measured on the monotonic clock; the UTC deadline is kept for a restart). Null once no
    /// channel is owed it any more or retries are given up.
    /// </summary>
    private PendingDelivery? NextAttempt(
        PendingDelivery current, IReadOnlyList<(string Channel, string Message)> failed, DateTime now)
    {
        var notification = current.Notification;
        var firstAttempt = current.FirstAttemptAt ?? now;
        var retries = current.ChannelRetries.Where(r => current.IsPendingOn(r.Channel)).ToList();
        failed = failed.Where(f => current.IsPendingOn(f.Channel)).ToList();
        if (failed.Count > 0)
        {
            var attempts = current.FailedAttempts + 1;
            var error = string.Join("; ", failed.Select(f => $"{f.Channel}: {f.Message}"));
            if (!current.IsAboutOpenAlert && now - firstAttempt >= NotificationRetryPolicy.GiveUpAfter)
            {
                _logger.LogError("Giving up on the {Kind} notification for {Key} after {Attempts} failed attempts: {Error}",
                    notification.Kind, notification.AlertKey, attempts, error);
                return null;
            }

            foreach (var (channel, message) in failed)
            {
                var previous = retries.FirstOrDefault(r => string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase));
                if (previous is not null)
                    retries.Remove(previous);
                var channelAttempts = (previous?.Attempts ?? 0) + 1;
                var backoff = NotificationRetryPolicy.Backoff(channelAttempts);
                retries.Add(new ChannelRetry(channel, channelAttempts, now + backoff, message)
                {
                    Due = MonotonicDeadline.After(_time, backoff),
                });
            }

            var retryAt = retries.Min(r => r.NextAttemptAt);
            _logger.LogWarning("{Kind} notification for {Key} not delivered everywhere (attempt {Attempts}); retrying at {RetryAt:O}: {Error}",
                notification.Kind, notification.AlertKey, attempts, retryAt, error);
            return current with
            {
                FailedAttempts = attempts,
                FirstAttemptAt = firstAttempt,
                NextAttemptAt = retryAt,
                LastError = error,
                ChannelRetries = retries,
            };
        }

        if (!current.PendingChannels.Any())
            return null; // every channel it is owed has it (or was skipped)

        // Channels still to go: behind an earlier notice of the alert, on their own retry
        // deadline, or held back by their rate limit or more urgent notices.
        return current with
        {
            FirstAttemptAt = firstAttempt,
            NextAttemptAt = retries.Count > 0 ? retries.Min(r => r.NextAttemptAt) : now,
            ChannelRetries = retries,
        };
    }

    /// <summary>
    /// Gives up on the resolved notices and events whose retries have run out
    /// (<see cref="PendingDelivery.IsExpired"/>) on every channel still missing them, so the
    /// alert's later notices proceed on those channels, and drops deliveries no channel is owed
    /// any more (see <see cref="SkipUnconfiguredLocked"/>). Held and in-flight deliveries are left
    /// as they are.
    /// </summary>
    private void ExpireLocked(DateTime now)
    {
        SkipUnconfiguredLocked();
        var deliveries = Deliveries();
        foreach (var d in deliveries.Values.ToList())
        {
            if (d.Held || _inFlight.Contains(d.Id) || !d.IsExpired(now))
                continue;
            _logger.LogError("Giving up on the {Kind} notification for {Key} after {Attempts} failed attempts; not delivered to {Channels}: {Error}",
                d.Notification.Kind, d.AlertKey, d.FailedAttempts, string.Join(", ", d.PendingChannels), d.LastError);
            _store.DeleteDelivery(d.Id);
            deliveries.Remove(d.Id);
        }
    }

    // ── Channel circuits ───────────────────────────────────────────────────

    private ChannelRateLimiter Limiter()
    {
        if (_limiter is not null)
            return _limiter;

        var limiter = new ChannelRateLimiter(_time, () => CriticalProbeCooldown);
        try
        {
            // Probe times are clamped to now + the circuit's backoff (see ChannelRateLimiter.Restore).
            if (_store.GetValue(ChannelCircuitsKey) is { } json &&
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ChannelCircuit>>(json) is { } circuits)
                limiter.Restore(circuits);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the notification channels' retry state; starting with every channel available");
        }
        return _limiter = limiter;
    }

    /// <summary>Persists the open circuits, so a restart does not reset a failing channel's backoff.</summary>
    private void PersistCircuitsLocked()
    {
        try
        {
            _store.SetValue(ChannelCircuitsKey, System.Text.Json.JsonSerializer.Serialize(Limiter().Snapshot()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the notification channels' retry state");
        }
    }

    /// <summary>The open alert's notice reached its first channel: it now counts as notified.</summary>
    private void MarkNotified(PendingDelivery delivery, DateTime now)
    {
        lock (_gate)
        {
            if (States().GetValueOrDefault(delivery.AlertKey) is not { IsOpen: true } state ||
                state.Occurrence != delivery.Occurrence)
                return; // the occurrence it was about is over
            Save(state with { LastNotifiedAt = now, NotifyCount = state.NotifyCount + 1 });
        }
    }

    /// <summary>
    /// The opened, reminder or resolved notice still being delivered for one occurrence of an
    /// alert (at most one per occurrence: a newer one of the same occurrence replaces it).
    /// </summary>
    private PendingDelivery? PendingNotice(string key, long occurrence)
    {
        lock (_gate)
            return PendingNoticeLocked(key, occurrence);
    }

    private PendingDelivery? PendingNoticeLocked(string key, long occurrence) =>
        Deliveries().Values.FirstOrDefault(d => IsNoticeOf(d, key, occurrence));

    private static bool IsNoticeOf(PendingDelivery d, string key, long occurrence) =>
        d.IsAlertNotice && !d.IsFlapping && d.Occurrence == occurrence &&
        string.Equals(d.AlertKey, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>The alert notices queued for <paramref name="key"/>, in the order they are sent.</summary>
    private List<PendingDelivery> KeyQueueLocked(string key) =>
        Deliveries().Values
            .Where(d => d.IsAlertNotice && string.Equals(d.AlertKey, key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.QueueOrder)
            .ToList();

    /// <summary>Takes the per-key lifecycle lock (see <see cref="_keyLocks"/>).</summary>
    private Task<IDisposable> LockKeyAsync(AlertKey key, CancellationToken ct) => _keyLocks.LockAsync(key.Value, ct);

    /// <summary>
    /// Removes the pending notice of one occurrence of an alert (superseded by a newer notice of
    /// that occurrence, or dropped). Other occurrences' notices are never touched here.
    /// </summary>
    private void RemoveNoticesLocked(string key, long occurrence)
    {
        var deliveries = Deliveries();
        foreach (var old in deliveries.Values.Where(d => IsNoticeOf(d, key, occurrence)).ToList())
        {
            _store.DeleteDelivery(old.Id);
            deliveries.Remove(old.Id);
        }
    }

    // ── State ──────────────────────────────────────────────────────────────

    private AlertState? TryGet(AlertKey key)
    {
        lock (_gate)
            return States().GetValueOrDefault(key.Value);
    }

    private void Save(AlertState state)
    {
        lock (_gate)
        {
            _store.Upsert(state);
            States()[state.Key] = state;
        }
    }

    private Dictionary<string, AlertState> States()
    {
        // Loaded lazily (and under the gate) so construction never touches the database.
        if (_states is not null)
            return _states;

        var states = new Dictionary<string, AlertState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var state in _store.LoadAll())
                states[state.Key] = state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load persisted alert state; starting with no open alerts");
        }
        return _states = states;
    }

    private Dictionary<string, PendingDelivery> Deliveries()
    {
        if (_deliveries is not null)
            return _deliveries;

        var deliveries = new Dictionary<string, PendingDelivery>(StringComparer.Ordinal);
        try
        {
            foreach (var loaded in _store.LoadDeliveries())
            {
                var delivery = Restored(loaded, out var changed);
                if (changed)
                {
                    try
                    {
                        _store.UpsertDelivery(delivery);
                    }
                    catch (Exception ex)
                    {
                        // Derived again (from the then current configuration) on the next start.
                        _logger.LogWarning(ex, "Could not update the restored notification delivery {Id}", delivery.Id);
                    }
                }
                deliveries[delivery.Id] = delivery;
                _lastSequence = Math.Max(_lastSequence, delivery.Sequence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load pending notification deliveries; starting with none");
        }
        _deliveries = deliveries;
        Limiter(); // the circuits are restored (and clamped) at the same moment as the retries
        return deliveries;
    }

    /// <summary>
    /// A delivery as loaded (after a restart), made ready for this process.
    /// <list type="bullet">
    /// <item>A row from before channel obligations were recorded (null
    /// <see cref="PendingDelivery.Channels"/>) is owed to the channels that take it under the
    /// configuration of this start, plus the ones it already reached (still delivered); written
    /// back, so this happens once.</item>
    /// <item>Every persisted retry deadline is clamped to now + its backoff and becomes a monotonic
    /// deadline (<see cref="ChannelRetry.Due"/>): a clock that went back while the agent was down
    /// cannot extend the wait beyond the policy, and one that jumped ahead only makes the retries
    /// due, which their channels' circuits still pace.</item>
    /// <item>Its aging restarts from how long it has waited so far (by its UTC creation time,
    /// bounded), on the monotonic clock.</item>
    /// </list>
    /// </summary>
    private PendingDelivery Restored(PendingDelivery loaded, out bool changed)
    {
        var utcNow = UtcNow;
        changed = false;
        var delivery = loaded;
        if (delivery.Channels is null)
        {
            delivery = delivery with
            {
                Channels = DistinctChannels((_dispatcher.ChannelsFor(delivery.Notification) ?? []).Concat(delivery.DeliveredChannels)),
            };
            changed = true;
        }

        var retries = new List<ChannelRetry>(delivery.ChannelRetries.Count);
        foreach (var retry in delivery.ChannelRetries)
        {
            var remaining = MonotonicDeadline.Clamp(retry.NextAttemptAt - utcNow, NotificationRetryPolicy.Backoff(retry.Attempts));
            var nextAttempt = retry.NextAttemptAt - utcNow > remaining ? utcNow + remaining : retry.NextAttemptAt;
            changed |= nextAttempt != retry.NextAttemptAt;
            retries.Add(retry with { NextAttemptAt = nextAttempt, Due = MonotonicDeadline.After(_time, remaining) });
        }

        var maxAge = AgingInterval * (int)AlertSeverity.Critical;
        var waited = MonotonicDeadline.Clamp(utcNow - delivery.CreatedAt, maxAge);
        var nextAttemptAt = retries.Count > 0 && delivery.NextAttemptAt > retries.Max(r => r.NextAttemptAt)
            ? retries.Min(r => r.NextAttemptAt)
            : delivery.NextAttemptAt;
        changed |= nextAttemptAt != delivery.NextAttemptAt;
        return delivery with
        {
            ChannelRetries = retries,
            NextAttemptAt = nextAttemptAt,
            EnqueuedAt = _time.GetTimestamp() - (long)(waited.TotalSeconds * _time.TimestampFrequency),
        };
    }
}

/// <summary>Shared wording helpers for alert notifications.</summary>
public static class AlertText
{
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration < TimeSpan.FromMinutes(1))
            return $"{(int)duration.TotalSeconds}s";
        if (duration < TimeSpan.FromHours(1))
            return $"{(int)duration.TotalMinutes} min";
        if (duration < TimeSpan.FromDays(1))
            return duration.Minutes == 0 ? $"{(int)duration.TotalHours} h" : $"{(int)duration.TotalHours} h {duration.Minutes} min";
        return duration.Hours == 0 ? $"{(int)duration.TotalDays} d" : $"{(int)duration.TotalDays} d {duration.Hours} h";
    }

    public static string Label(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        _ => "info",
    };
}