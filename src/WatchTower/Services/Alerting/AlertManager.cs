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
    /// <see cref="_gate"/>). An alert's notices are sent one at a time, in queue order (see
    /// <see cref="ClaimHeadLocked"/>): a later notice for the same alert (its recovery, an escalation,
    /// the next occurrence's opening) is not claimed while an earlier one is in flight, and the
    /// sender of the earlier one sends it as soon as that dispatch has finished (see
    /// <see cref="ClaimSuccessorLocked"/>). Nobody waits for it, so a slow channel never blocks a
    /// monitor worker, other alerts or a shutdown.
    /// </summary>
    private readonly Dictionary<string, PendingDelivery> _sending = new(StringComparer.OrdinalIgnoreCase);

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

    public AlertManager(
        IAlertStateStore store,
        IMetricsStore metricsStore,
        IPortalEventPublisher portal,
        INotificationDispatcher dispatcher,
        IOptionsMonitor<AlertingOptions> options,
        TimeProvider time,
        ILogger<AlertManager> logger,
        IMaintenanceState? maintenance = null,
        string? serverName = null)
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
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public TimeSpan RecoveryWindow => TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.ResolveAfterSeconds));

    public bool IsOpen(AlertKey key) => TryGet(key) is { IsOpen: true };

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
            var pending = PendingNotice(key.Value, existing!.Occurrence);
            var reminderDue = IsReminderDue(existing, now, options);
            // A notice still on its way (held, or not yet delivered anywhere) is finished rather
            // than replaced. One that reached some channels is replaced by the next reminder, so a
            // permanently failing channel cannot suppress reminders to the working ones. It may be
            // queued behind an earlier occurrence's recovery: the alert's queue is continued.
            if (pending is not null && (pending.Held || pending.DeliveredChannels.Count == 0 || !reminderDue))
            {
                if (!inMaintenance)
                    await ContinueKeyAsync(key.Value, now, ct);
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
        return CloseAsync(key, recoveryIncident: null, reason, ct);
    }

    public async Task<IReadOnlyList<string>> RetireUnconfiguredAsync(
        string keyPrefix, IEnumerable<AlertKey> configured, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var keep = configured.Select(k => k.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        PendingDelivery? claimed = null; // the recovery, or a notice of the alert queued before it

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
                recovery = EnqueueLocked(notification, occurrence, now, held: inMaintenance, out claimed,
                    explicitRecovery: true);
            }
            else if (options.SendRecovery && (inMaintenance || pending is { Held: true }))
            {
                // Opened and cleared while notifications were held: kept for the end-of-window summary.
                recovery = EnqueueLocked(notification, occurrence, now, held: true, out _);
            }
            else
            {
                RemoveNoticesLocked(key.Value, occurrence);
            }
        }

        // The claim (if any) is ours until SendOrLogHeldAsync takes it over: whatever
        // fails before that (the portal, the metrics store) gives it back, and the recovery stays
        // pending for the delivery worker.
        var handedOver = false;
        try
        {
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

            handedOver = true;
            if (recovery is not null)
                await SendOrLogHeldAsync(recovery, claimed, ct);
        }
        finally
        {
            if (!handedOver && claimed is not null)
                Unclaim(claimed);
        }
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
            List<string> due;
            lock (_gate)
            {
                SummariseHeldLocked(now);
                // Creation order, which is also each alert's queue order; a notice whose turn has
                // not come yet is sent by the sender of the one before it (or the next pass).
                due = Deliveries().Values
                    .Where(d => d.Held || d.NextAttemptAt <= now)
                    .OrderBy(d => d.Sequence)
                    .Select(d => d.Id)
                    .ToList();
            }

            foreach (var id in due)
            {
                ct.ThrowIfCancellationRequested();
                // A window that starts while the batch is drained holds the rest of it (checked
                // again right before each send, see PrepareLocked).
                if (_maintenance.Current.Active)
                    return;
                await ContinueAsync(id, now, ct);
            }
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
    /// <paramref name="held"/>, attempts it right away.
    /// </summary>
    private async Task DeliverAsync(
        AlertNotification notification, long occurrence, DateTime now, bool held, CancellationToken ct)
    {
        PendingDelivery delivery;
        PendingDelivery? claimed;
        lock (_gate)
            delivery = EnqueueLocked(notification, occurrence, now, held, out claimed);
        await SendOrLogHeldAsync(delivery, claimed, ct);
    }

    /// <summary>
    /// Persists a new delivery and, unless held, claims it in the same section, before
    /// <see cref="ProcessPendingAsync"/> or a close can see it unclaimed. An alert notice goes to
    /// the end of its key's queue: it replaces the pending notice of the same
    /// <paramref name="occurrence"/> (a reminder, an escalation or the recovery superseding the
    /// opened notice) but never an earlier occurrence's, and the queue is kept within
    /// <see cref="MaxQueuedNotices"/>.
    /// <para>
    /// For an alert notice the head of its key's queue is claimed (see
    /// <see cref="ClaimHeadLocked"/>), which may be an earlier notice: its sender sends the new
    /// one after it. <paramref name="claimed"/> is null when nothing can be sent now (held, or
    /// behind a notice in flight, whose sender sends it next).
    /// </para>
    /// </summary>
    private PendingDelivery EnqueueLocked(
        AlertNotification notification, long occurrence, DateTime now, bool held, out PendingDelivery? claimed,
        bool explicitRecovery = false)
    {
        var isNotice = notification.Kind is AlertNotificationKind.Opened or AlertNotificationKind.Reminder
            or AlertNotificationKind.Resolved;
        if (isNotice)
        {
            RemoveNoticesLocked(notification.AlertKey, occurrence);
            // Before the new notice takes its sequence number, so the flapping notice sorts
            // before it in creation order too.
            CoalesceLocked(notification.AlertKey, now);
        }

        var delivery = PendingDelivery.Create(notification, now, held, explicitRecovery,
            isNotice ? occurrence : 0, NextSequenceLocked());
        _store.UpsertDelivery(delivery);
        Deliveries()[delivery.Id] = delivery;
        claimed = held ? null
            : isNotice ? ClaimHeadLocked(delivery.AlertKey, now, includeHeld: false)
            : TryClaimLocked(delivery) ? delivery : null;
        return delivery;
    }

    /// <summary>
    /// Starts sending <paramref name="key"/>'s queue: claims its head (the earliest notice, see
    /// <see cref="PendingDelivery.QueueOrder"/>) if any notice of the queue is due, or held and
    /// <paramref name="includeHeld"/> (to be released). The head is claimed even if it is itself
    /// still backing off: a notice is never sent before the ones queued ahead of it, which are
    /// retried early instead (see <see cref="ClaimSuccessorLocked"/>).
    /// </summary>
    private PendingDelivery? ClaimHeadLocked(string key, DateTime now, bool includeHeld)
    {
        var queue = KeyQueueLocked(key);
        if (queue.Count == 0 || (queue[0].Held && !includeHeld) ||
            !queue.Any(d => d.Held ? includeHeld : d.NextAttemptAt <= now))
            return null;
        return TryClaimLocked(queue[0]) ? queue[0] : null;
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
    /// superseded one) and the flapping notice, sent to every channel, covers it. The flapping
    /// notice is explicit (sent on its own) if any folded notice was; one standing only for
    /// occurrences operators were never told about stays eligible for the maintenance summary.
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
        { FlappingCount = count };

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
    /// Sends what <see cref="EnqueueLocked"/> claimed for <paramref name="delivery"/> (itself, or a
    /// notice queued before it, followed by the rest of the alert's queue), or logs why it is not
    /// sent now: it is held, or queued behind the alert's earlier notices.
    /// </summary>
    private async Task SendOrLogHeldAsync(PendingDelivery delivery, PendingDelivery? claimed, CancellationToken ct)
    {
        if (claimed is not null)
        {
            await AttemptClaimedAsync(claimed, ct);
            return;
        }

        if (delivery.Held)
        {
            _logger.LogInformation("{Kind} notification for alert {Key} held: maintenance window active until {Until:O}",
                delivery.Notification.Kind, delivery.AlertKey, _maintenance.Current.Until);
            return;
        }

        _logger.LogDebug("{Kind} notification for alert {Key} queued behind the alert's earlier notices",
            delivery.Notification.Kind, delivery.AlertKey);
    }

    /// <summary>
    /// Claims <paramref name="delivery"/> for sending, unless it is already claimed or (alert
    /// notices) another notice of the same alert is being sent (see <see cref="_sending"/>). Which
    /// notice of an alert's queue may be claimed is decided by the callers
    /// (<see cref="ClaimHeadLocked"/>, <see cref="ClaimSuccessorLocked"/>).
    /// </summary>
    private bool TryClaimLocked(PendingDelivery delivery)
    {
        if (_inFlight.Contains(delivery.Id))
            return false;
        if (delivery.IsAlertNotice && !_sending.TryAdd(delivery.AlertKey, delivery))
            return false;
        _inFlight.Add(delivery.Id);
        return true;
    }

    private void ReleaseClaimLocked(PendingDelivery claimed)
    {
        _inFlight.Remove(claimed.Id);
        if (_sending.GetValueOrDefault(claimed.AlertKey) is { } sending && sending.Id == claimed.Id)
            _sending.Remove(claimed.AlertKey);
    }

    /// <summary>
    /// After <paramref name="finished"/> (an alert notice) was sent: claims the next notice of the
    /// alert's queue (its recovery, an escalation, the next occurrence's opening) for the same
    /// sender to send next, if it or a notice after it is due. <paramref name="attempted"/> holds
    /// the notices this sender already attempted: one that failed again stays queued for its
    /// retry and no longer holds back the ones behind it.
    /// <para>
    /// So an alert's queue is always sent from its head, in order, one notice at a time: a notice
    /// is never attempted before every notice queued ahead of it was attempted in the same pass
    /// (retried early if it was still backing off). Each channel gets an alert's notices in order
    /// (the first occurrence's recovery before the next one's opening), and a channel that keeps
    /// failing cannot hold back a new alert on the channels that work. A held successor is taken
    /// too once no window is active (it is released when it is prepared, see
    /// <see cref="PrepareLocked"/>).
    /// </para>
    /// </summary>
    private PendingDelivery? ClaimSuccessorLocked(PendingDelivery finished, IReadOnlySet<string> attempted)
    {
        if (!finished.IsAlertNotice)
            return null;
        var now = UtcNow;
        var includeHeld = !_maintenance.Current.Active;
        var rest = KeyQueueLocked(finished.AlertKey).Where(d => !attempted.Contains(d.Id)).ToList();
        if (rest.Count == 0 || !rest.Any(d => d.Held ? includeHeld : d.NextAttemptAt <= now))
            return null;
        return TryClaimLocked(rest[0]) ? rest[0] : null;
    }

    /// <summary>
    /// Releases a held delivery or retries a due one, unless another caller is already sending it
    /// or a maintenance window is active (it then stays pending, and held, until the window ends).
    /// An alert notice is sent with its key's queue, from the head (see <see cref="ClaimHeadLocked"/>).
    /// </summary>
    private async Task ContinueAsync(string id, DateTime now, CancellationToken ct)
    {
        // Shortcut: a window being active leaves it as it is (PrepareLocked checks again at the send).
        if (_maintenance.Current.Active)
            return;

        PendingDelivery? delivery;
        lock (_gate)
        {
            // Read and claim in one section: a close in between would otherwise miss this send.
            // Nothing that can fail happens here once the claim is taken: releasing a held
            // delivery (a store write) is done by AttemptClaimedAsync, which always gives the
            // claim back.
            delivery = Deliveries().GetValueOrDefault(id);
            if (delivery is { IsAlertNotice: true })
                delivery = ClaimHeadLocked(delivery.AlertKey, now, includeHeld: true);
            else if (delivery is null || (!delivery.Held && delivery.NextAttemptAt > now) || !TryClaimLocked(delivery))
                return;
        }

        if (delivery is not null)
            await AttemptClaimedAsync(delivery, ct);
    }

    /// <summary>Sends <paramref name="key"/>'s queue if any of its notices is held or due.</summary>
    private async Task ContinueKeyAsync(string key, DateTime now, CancellationToken ct)
    {
        if (_maintenance.Current.Active)
            return;

        PendingDelivery? claimed;
        lock (_gate)
            claimed = ClaimHeadLocked(key, now, includeHeld: true);
        if (claimed is not null)
            await AttemptClaimedAsync(claimed, ct);
    }

    /// <summary>Gives back a claim whose send never started.</summary>
    private void Unclaim(PendingDelivery claimed)
    {
        lock (_gate)
            ReleaseClaimLocked(claimed);
    }

    /// <summary>Most held items listed one by one in the end-of-window summary.</summary>
    internal const int MaxSummaryLines = 20;

    /// <summary>
    /// A maintenance window ended. Held notices operators should get one by one stay as they are
    /// (alerts still open, and resolutions of alerts whose opening may have reached operators); they
    /// are released by <see cref="ContinueAsync"/>. Everything else that was held — alerts that
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
            var delivery = PendingDelivery.Create(summary, now, held: false, sequence: NextSequenceLocked()) with { Id = id };
            _store.UpsertDelivery(delivery);
            deliveries[id] = delivery;
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
    /// Sends <paramref name="delivery"/> to every eligible channel that does not have it yet and
    /// records the outcome: delivered channels are never sent it again, failed ones are retried
    /// with <see cref="NotificationRetryPolicy"/>. The caller holds the in-flight claim; it is
    /// released here on every path, including a failure while the send is prepared (releasing a
    /// held delivery) or its outcome is persisted.
    /// <para>
    /// Then sends the alert's notices queued behind it, if any (see <see cref="_sending"/> and
    /// <see cref="ClaimSuccessorLocked"/>), each at most once. Only after a dispatch that finished:
    /// one cut short by <paramref name="ct"/> (a shutdown) leaves the queued notices pending for
    /// the delivery worker or the next start.
    /// </para>
    /// </summary>
    private async Task AttemptClaimedAsync(PendingDelivery claimed, CancellationToken ct)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        for (PendingDelivery? next = claimed; next is not null;)
        {
            var current = next;
            next = null;
            attempted.Add(current.Id);
            var finished = false;
            try
            {
                await SendClaimedAsync(current, ct);
                finished = true;
            }
            finally
            {
                // Release and hand over in one section: a notice queued behind this one can
                // neither be skipped nor be claimed by two senders.
                lock (_gate)
                {
                    ReleaseClaimLocked(current);
                    if (finished && !ct.IsCancellationRequested)
                        next = ClaimSuccessorLocked(current, attempted);
                }
            }
        }
    }

    /// <summary>One attempt of <see cref="AttemptClaimedAsync"/> (the caller releases the claim).</summary>
    private async Task SendClaimedAsync(PendingDelivery claimed, CancellationToken ct)
    {
        PendingDelivery? prepared;
        lock (_gate)
            prepared = PrepareLocked(claimed);
        if (prepared is null)
            return;

        PendingDelivery delivery = prepared;
        var notification = delivery.Notification;
        var skip = new HashSet<string>(delivery.DeliveredChannels, StringComparer.OrdinalIgnoreCase);
        var reached = delivery.DeliveredChannels.Count > 0; // guarded by _gate

        // Each channel that delivers is recorded right away, so a dispatch cut short (e.g. by
        // a graceful shutdown waiting on a slow channel) never loses, or later repeats, it.
        void OnChannelCompleted(ChannelDelivery channel)
        {
            if (!channel.Success)
                return;
            try
            {
                lock (_gate)
                    RecordDeliveredLocked(delivery, channel.Channel, ref reached);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not record that the {Kind} notification for alert {Key} reached {Channel}",
                    notification.Kind, notification.AlertKey, channel.Channel);
            }
        }

        DispatchResult result;
        string? error = null;
        try
        {
            result = await _dispatcher.DispatchAsync(notification, skip, OnChannelCompleted, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Dispatching the {Kind} notification for alert {Key} failed",
                notification.Kind, notification.AlertKey);
            result = DispatchResult.None;
            error = $"Dispatch failed ({ex.GetType().Name})";
        }

        var now = UtcNow;
        var failed = result.Failed.ToList();
        if (error is null && failed.Count > 0)
            error = string.Join("; ", failed.Select(f => $"{f.Channel}: {f.Message}"));
        var firstAttempt = delivery.FirstAttemptAt ?? now;

        lock (_gate)
        {
            // Channels a dispatcher reported only in its result (not as they completed).
            foreach (var channel in result.Delivered)
                RecordDeliveredLocked(delivery, channel, ref reached);

            // Superseded while it was being sent: the newer notice of its occurrence, the close or
            // the flapping notice it was folded into owns the row.
            if (Deliveries().GetValueOrDefault(delivery.Id) is not { } current)
                return;
            var next = NextAttempt(current, result, error, firstAttempt, now);
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

        if (Deliveries().GetValueOrDefault(delivery.Id) is not { } current ||
            current.DeliveredChannels.Contains(channel, StringComparer.OrdinalIgnoreCase))
            return;
        var updated = current with { DeliveredChannels = [.. current.DeliveredChannels, channel] };
        _store.UpsertDelivery(updated);
        Deliveries()[delivery.Id] = updated;
    }

    /// <summary>
    /// What is left of <paramref name="current"/> (its delivered channels already recorded) after
    /// an attempt: null once every eligible channel has it or retries are given up.
    /// </summary>
    private PendingDelivery? NextAttempt(
        PendingDelivery current, DispatchResult result, string? error, DateTime firstAttempt, DateTime now)
    {
        var notification = current.Notification;
        if (error is not null)
        {
            var attempts = current.FailedAttempts + 1;
            if (!current.IsAboutOpenAlert && now - firstAttempt >= NotificationRetryPolicy.GiveUpAfter)
            {
                _logger.LogError("Giving up on the {Kind} notification for {Key} after {Attempts} failed attempts: {Error}",
                    notification.Kind, notification.AlertKey, attempts, error);
                return null;
            }

            var retryAt = now + NotificationRetryPolicy.Backoff(attempts);
            _logger.LogWarning("{Kind} notification for {Key} not delivered everywhere (attempt {Attempts}); retrying at {RetryAt:O}: {Error}",
                notification.Kind, notification.AlertKey, attempts, retryAt, error);
            return current with
            {
                FailedAttempts = attempts,
                FirstAttemptAt = firstAttempt,
                NextAttemptAt = retryAt,
                LastError = error,
            };
        }

        if (result.Channels.Count == 0 && current.DeliveredChannels.Count == 0 && current.IsAboutOpenAlert)
        {
            // No channel is configured or available for it yet: the alert stays pending and is
            // offered again at the next opportunity. Other notices are not worth keeping.
            return current with { FirstAttemptAt = firstAttempt, NextAttemptAt = now };
        }

        return null; // every eligible channel has it
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
            foreach (var delivery in _store.LoadDeliveries())
            {
                deliveries[delivery.Id] = delivery;
                _lastSequence = Math.Max(_lastSequence, delivery.Sequence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load pending notification deliveries; starting with none");
        }
        return _deliveries = deliveries;
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