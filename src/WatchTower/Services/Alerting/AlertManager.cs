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

    /// <summary>
    /// Deliveries being sent right now (by id), so no two callers send one at once. Guarded by
    /// <see cref="_gate"/>: a claim is taken in the same section that reads the delivery, and
    /// released only after its outcome (including <see cref="MarkNotified"/>) is recorded.
    /// </summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// The opened, reminder or resolved notice being sent right now for each alert key (guarded by
    /// <see cref="_gate"/>). An alert's notices are sent one at a time, in order: a later notice
    /// for the same alert (its recovery, an escalation) is not claimed while an earlier one is in
    /// flight, and the sender of the earlier one sends it as soon as that dispatch has finished
    /// (see <see cref="ClaimSuccessorLocked"/>). Nobody waits for it, so a slow channel never
    /// blocks a monitor worker, other alerts or a shutdown.
    /// </summary>
    private readonly Dictionary<string, PendingDelivery> _sending = new(StringComparer.OrdinalIgnoreCase);

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
            return Deliveries().Values.OrderBy(d => d.CreatedAt).ToList();
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
            var pending = PendingNotice(key.Value);
            var reminderDue = IsReminderDue(existing!, now, options);
            // A notice still on its way (held, or not yet delivered anywhere) is finished rather
            // than replaced. One that reached some channels is replaced by the next reminder, so a
            // permanently failing channel cannot suppress reminders to the working ones.
            if (pending is not null && (pending.Held || pending.DeliveredChannels.Count == 0 || !reminderDue))
            {
                if (!inMaintenance)
                    await ContinueAsync(pending.Id, now, ct);
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
                state = new AlertState(key.Value, severity, incident.Type, title, now,
                    LastNotifiedAt: null, NotifyCount: 0, ResolvedAt: null, incident.Description);
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

        await DeliverAsync(notification, now, inMaintenance, ct);
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
        TimeSpan duration;
        string description;
        PendingDelivery? recovery = null;
        var recoveryClaimed = false;

        // One section from reading the alert to replacing its notice: a delivery cannot claim the
        // opened notice, or count it as notified, in between.
        lock (_gate)
        {
            if (States().GetValueOrDefault(key.Value) is not { IsOpen: true } open)
                return false;

            existing = open;
            duration = now - existing.OpenedAt;
            description = retiredReason is not null
                ? $"No longer monitored ({retiredReason}); closed after {AlertText.FormatDuration(duration)}. " +
                  $"Last state: {existing.LastDescription}"
                : recoveryIncident?.Description
                  // No dedicated recovery incident type (CPU, disks, queues).
                  ?? $"Resolved after {AlertText.FormatDuration(duration)}: {existing.LastDescription}";

            Save(existing with { ResolvedAt = now, RetiredReason = retiredReason });

            // An opened notice that failed on every channel so far still tells its story as a
            // resolved notice, and so does one being sent right now (it may reach operators any
            // moment; it is superseded below, so it cannot count as notified afterwards). One that
            // was never attempted on any channel is dropped silently.
            var pending = PendingNoticeLocked(key.Value);
            var attempted = (pending is { IsAboutOpenAlert: true } &&
                             (pending.FailedAttempts > 0 || pending.DeliveredChannels.Count > 0)) ||
                            (_sending.GetValueOrDefault(key.Value) is { IsAboutOpenAlert: true } sending &&
                             sending.Notification.OpenedAtUtc == existing.OpenedAt);

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
                recovery = EnqueueLocked(notification, now, held: inMaintenance, out recoveryClaimed, explicitRecovery: true);
            }
            else if (options.SendRecovery && (inMaintenance || pending is { Held: true }))
            {
                // Opened and cleared while notifications were held: kept for the end-of-window summary.
                recovery = EnqueueLocked(notification, now, held: true, out _);
            }
            else
            {
                RemoveNoticesLocked(key.Value);
            }
        }

        // The recovery's claim (if any) is ours until SendOrLogHeldAsync takes it over: whatever
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
                await SendOrLogHeldAsync(recovery, recoveryClaimed, ct);
        }
        finally
        {
            if (!handedOver && recoveryClaimed)
                Unclaim(recovery!);
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
            incident.Timestamp, Incident: incident), UtcNow, held: _maintenance.Current.Active, ct);
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
                due = Deliveries().Values
                    .Where(d => d.Held || d.NextAttemptAt <= now)
                    .OrderBy(d => d.CreatedAt)
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
    /// alert's previous one) and, unless <paramref name="held"/>, attempts it right away.
    /// </summary>
    private async Task DeliverAsync(AlertNotification notification, DateTime now, bool held, CancellationToken ct)
    {
        PendingDelivery delivery;
        bool claimed;
        lock (_gate)
            delivery = EnqueueLocked(notification, now, held, out claimed);
        await SendOrLogHeldAsync(delivery, claimed, ct);
    }

    /// <summary>
    /// Persists a new delivery (replacing the alert's previous notice) and, unless held, claims it
    /// in the same section, before <see cref="ProcessPendingAsync"/> or a close can see it unclaimed.
    /// It is not claimed (<paramref name="claimed"/> false) while an earlier notice of the same
    /// alert is still being sent: that sender sends it next.
    /// </summary>
    private PendingDelivery EnqueueLocked(
        AlertNotification notification, DateTime now, bool held, out bool claimed, bool explicitRecovery = false)
    {
        var delivery = PendingDelivery.Create(notification, now, held, explicitRecovery);
        if (delivery.IsAlertNotice)
            RemoveNoticesLocked(notification.AlertKey);
        _store.UpsertDelivery(delivery);
        Deliveries()[delivery.Id] = delivery;
        claimed = !held && TryClaimLocked(delivery);
        return delivery;
    }

    /// <summary>
    /// Sends a delivery claimed by <see cref="EnqueueLocked"/>, or logs why it is not sent now: it
    /// is held, or queued behind the alert's notice being sent.
    /// </summary>
    private async Task SendOrLogHeldAsync(PendingDelivery delivery, bool claimed, CancellationToken ct)
    {
        if (claimed)
        {
            await AttemptClaimedAsync(delivery, ct);
            return;
        }

        if (delivery.Held)
        {
            _logger.LogInformation("{Kind} notification for alert {Key} held: maintenance window active until {Until:O}",
                delivery.Notification.Kind, delivery.AlertKey, _maintenance.Current.Until);
            return;
        }

        _logger.LogDebug("{Kind} notification for alert {Key} queued behind the alert's notice being sent",
            delivery.Notification.Kind, delivery.AlertKey);
    }

    /// <summary>
    /// Claims <paramref name="delivery"/> for sending, unless it is already claimed or (alert
    /// notices) another notice of the same alert is being sent (see <see cref="_sending"/>).
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
    /// After <paramref name="finished"/> (an alert notice) was sent: claims the alert's notice that
    /// was queued behind it (its recovery, an escalation), if one is due, for the same sender to
    /// send next. Held notices and retries still backing off are left to the delivery worker.
    /// </summary>
    private PendingDelivery? ClaimSuccessorLocked(PendingDelivery finished)
    {
        if (!finished.IsAlertNotice ||
            PendingNoticeLocked(finished.AlertKey) is not { } next ||
            next.Id == finished.Id || next.Held || next.NextAttemptAt > UtcNow)
            return null;
        return TryClaimLocked(next) ? next : null;
    }

    /// <summary>
    /// Releases a held delivery or retries a due one, unless another caller is already sending it
    /// or a maintenance window is active (it then stays pending, and held, until the window ends).
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
            if (delivery is null || (!delivery.Held && delivery.NextAttemptAt > now) || !TryClaimLocked(delivery))
                return;
        }

        await AttemptClaimedAsync(delivery, ct);
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
            var lines = held.Take(MaxSummaryLines).Select(d => SummaryLine(d.Notification)).ToList();
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
            var delivery = PendingDelivery.Create(summary, now, held: false) with { Id = id };
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

    private static string SummaryLine(AlertNotification n)
    {
        var at = DateTime.SpecifyKind(n.OccurredAtUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        var label = AlertText.Label(n.Severity);
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
    /// for how long, latest description) and sent; one whose alert is no longer open is dropped.
    /// </summary>
    private PendingDelivery? ReleaseLocked(PendingDelivery delivery, DateTime now)
    {
        var released = delivery with { Held = false, NextAttemptAt = now };
        if (delivery.IsAboutOpenAlert)
        {
            var state = States().GetValueOrDefault(delivery.AlertKey);
            if (state is not { IsOpen: true } || state.OpenedAt != delivery.Notification.OpenedAtUtc)
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
    /// Then sends the alert's notice queued behind it, if any (see <see cref="_sending"/>), and so
    /// on. Only after a dispatch that finished: one cut short by <paramref name="ct"/> (a shutdown)
    /// leaves the queued notice pending for the delivery worker or the next start.
    /// </para>
    /// </summary>
    private async Task AttemptClaimedAsync(PendingDelivery claimed, CancellationToken ct)
    {
        for (PendingDelivery? next = claimed; next is not null;)
        {
            var current = next;
            next = null;
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
                        next = ClaimSuccessorLocked(current);
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

            // Superseded while it was being sent: the newer notice or the close owns the row.
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
            MarkNotified(delivery.Notification, UtcNow);
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
    private void MarkNotified(AlertNotification notification, DateTime now)
    {
        lock (_gate)
        {
            if (States().GetValueOrDefault(notification.AlertKey) is not { IsOpen: true } state ||
                state.OpenedAt != notification.OpenedAtUtc)
                return; // the occurrence it was about is over
            Save(state with { LastNotifiedAt = now, NotifyCount = state.NotifyCount + 1 });
        }
    }

    /// <summary>The opened, reminder or resolved notice still being delivered for an alert.</summary>
    private PendingDelivery? PendingNotice(string key)
    {
        lock (_gate)
            return PendingNoticeLocked(key);
    }

    private PendingDelivery? PendingNoticeLocked(string key) =>
        Deliveries().Values.FirstOrDefault(d =>
            d.IsAlertNotice && string.Equals(d.AlertKey, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Takes the per-key lifecycle lock (see <see cref="_keyLocks"/>).</summary>
    private Task<IDisposable> LockKeyAsync(AlertKey key, CancellationToken ct) => _keyLocks.LockAsync(key.Value, ct);

    private void RemoveNoticesLocked(string key)
    {
        var deliveries = Deliveries();
        foreach (var old in deliveries.Values
                     .Where(d => d.IsAlertNotice && string.Equals(d.AlertKey, key, StringComparison.OrdinalIgnoreCase))
                     .ToList())
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
                deliveries[delivery.Id] = delivery;
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