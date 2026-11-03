using System.Collections.Concurrent;

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

    private readonly Lock _gate = new();
    private Dictionary<string, AlertState>? _states;
    private Dictionary<string, PendingDelivery>? _deliveries;

    /// <summary>Deliveries being attempted right now (by id), so no two callers send one at once.</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

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

    public async Task RaiseAsync(
        AlertKey key,
        AlertSeverity severity,
        string title,
        Func<CancellationToken, Task<Incident>> incidentFactory,
        CancellationToken ct = default)
    {
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
        if (!open)
        {
            state = new AlertState(key.Value, severity, incident.Type, title, now,
                LastNotifiedAt: null, NotifyCount: 0, ResolvedAt: null, incident.Description);
        }
        else
        {
            state = existing! with
            {
                Severity = escalated ? severity : existing.Severity,
                IncidentType = escalated ? incident.Type : existing.IncidentType,
                Title = title,
                LastDescription = incident.Description,
            };
        }

        // Persist before notifying: a crash mid-send must not re-open (and re-send) the alert.
        Save(state);

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
        var existing = TryGet(key);
        if (existing is not { IsOpen: true })
            return false;

        var now = UtcNow;
        var duration = now - existing.OpenedAt;
        Save(existing with { ResolvedAt = now, RetiredReason = retiredReason });

        // An opened notice that failed on every channel so far still tells its story as a
        // resolved notice; one that was never attempted on any channel is dropped silently.
        var pending = PendingNotice(key.Value);
        var attempted = pending is { IsAboutOpenAlert: true, FailedAttempts: > 0 };

        string description;
        if (retiredReason is not null)
        {
            description = $"No longer monitored ({retiredReason}); closed after {AlertText.FormatDuration(duration)}. " +
                          $"Last state: {existing.LastDescription}";
            await _portal.PublishAlertEventAsync(
                new Incident(now, existing.IncidentType, description), key.Value, resolved: true, ct);
        }
        else if (recoveryIncident is not null)
        {
            description = recoveryIncident.Description;
            _metricsStore.AddIncident(recoveryIncident);
            await _portal.PublishAlertEventAsync(recoveryIncident, key.Value, resolved: true, ct);
        }
        else
        {
            // No dedicated recovery incident type (CPU, disks, queues): tell the portal the alert
            // of this type cleared. Not recorded locally, where it would read as a new breach.
            description = $"Resolved after {AlertText.FormatDuration(duration)}: {existing.LastDescription}";
            await _portal.PublishAlertEventAsync(
                new Incident(now, existing.IncidentType, description), key.Value, resolved: true, ct);
        }

        if (retiredReason is not null)
            _logger.LogInformation("Alert {Key} retired after {Duration}: {Reason}", key.Value, AlertText.FormatDuration(duration), retiredReason);
        else
            _logger.LogInformation("Alert {Key} resolved after {Duration}", key.Value, AlertText.FormatDuration(duration));

        var options = _options.CurrentValue;
        var inMaintenance = _maintenance.Current.Active;
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
            await DeliverAsync(notification, now, inMaintenance, ct);
        }
        else if (options.SendRecovery && (inMaintenance || pending is { Held: true }))
        {
            // Opened and cleared while notifications were held: kept for the end-of-window summary.
            await DeliverAsync(notification, now, held: true, ct);
        }
        else
        {
            RemoveNotices(key.Value);
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
        var delivery = PendingDelivery.Create(notification, now, held);
        if (!held)
            _inFlight.TryAdd(delivery.Id, 0); // claimed before it becomes visible to ProcessPendingAsync

        lock (_gate)
        {
            if (delivery.IsAlertNotice)
                RemoveNoticesLocked(notification.AlertKey);
            _store.UpsertDelivery(delivery);
            Deliveries()[delivery.Id] = delivery;
        }

        if (held)
        {
            _logger.LogInformation("{Kind} notification for alert {Key} held: maintenance window active until {Until:O}",
                notification.Kind, notification.AlertKey, _maintenance.Current.Until);
            return;
        }

        await AttemptClaimedAsync(delivery, ct);
    }

    /// <summary>
    /// Releases a held delivery or retries a due one, unless another caller is already sending it.
    /// </summary>
    private async Task ContinueAsync(string id, DateTime now, CancellationToken ct)
    {
        if (!_inFlight.TryAdd(id, 0))
            return;

        PendingDelivery? delivery;
        lock (_gate)
        {
            // Re-read under the claim: the snapshot the caller saw may be out of date.
            delivery = Deliveries().GetValueOrDefault(id);
            if (delivery is not null && delivery.Held)
                delivery = ReleaseLocked(delivery, now);
        }

        if (delivery is null || delivery.NextAttemptAt > now)
        {
            _inFlight.TryRemove(id, out _);
            return;
        }

        await AttemptClaimedAsync(delivery, ct);
    }

    /// <summary>Most held items listed one by one in the end-of-window summary.</summary>
    internal const int MaxSummaryLines = 20;

    /// <summary>
    /// A maintenance window ended. Held notices operators should get one by one stay as they are
    /// (alerts still open, and resolutions of alerts that were notified before the window); they
    /// are released by <see cref="ContinueAsync"/>. Everything else that was held — alerts that
    /// opened and cleared inside the window, one-shot events such as the patch reboot — is
    /// replaced by a single summary notification.
    /// </summary>
    private void SummariseHeldLocked(DateTime now)
    {
        var deliveries = Deliveries();
        var held = deliveries.Values
            .Where(d => d.Held && (d.Notification.Kind == AlertNotificationKind.Event ||
                                   d.Notification is { Kind: AlertNotificationKind.Resolved, NotifyCount: 0 }))
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
    /// with <see cref="NotificationRetryPolicy"/>. The caller holds the in-flight claim.
    /// </summary>
    private async Task AttemptClaimedAsync(PendingDelivery delivery, CancellationToken ct)
    {
        try
        {
            var notification = delivery.Notification;
            var skip = new HashSet<string>(delivery.DeliveredChannels, StringComparer.OrdinalIgnoreCase);

            DispatchResult result;
            string? error = null;
            try
            {
                result = await _dispatcher.DispatchAsync(notification, skip, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Dispatching the {Kind} notification for alert {Key} failed",
                    notification.Kind, notification.AlertKey);
                result = DispatchResult.None;
                error = $"Dispatch failed ({ex.GetType().Name})";
            }

            var now = UtcNow;
            var delivered = delivery.DeliveredChannels.Concat(result.Delivered)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var failed = result.Failed.ToList();
            if (error is null && failed.Count > 0)
                error = string.Join("; ", failed.Select(f => $"{f.Channel}: {f.Message}"));
            var firstAttempt = delivery.FirstAttemptAt ?? now;

            // Counted as notified once it actually reached a channel, not when it was attempted.
            if (delivery.DeliveredChannels.Count == 0 && delivered.Count > 0 && delivery.IsAboutOpenAlert)
                MarkNotified(notification, now);

            PendingDelivery? next;
            if (error is not null)
            {
                var attempts = delivery.FailedAttempts + 1;
                if (!delivery.IsAboutOpenAlert && now - firstAttempt >= NotificationRetryPolicy.GiveUpAfter)
                {
                    _logger.LogError("Giving up on the {Kind} notification for {Key} after {Attempts} failed attempts: {Error}",
                        notification.Kind, notification.AlertKey, attempts, error);
                    next = null;
                }
                else
                {
                    var retryAt = now + NotificationRetryPolicy.Backoff(attempts);
                    _logger.LogWarning("{Kind} notification for {Key} not delivered everywhere (attempt {Attempts}); retrying at {RetryAt:O}: {Error}",
                        notification.Kind, notification.AlertKey, attempts, retryAt, error);
                    next = delivery with
                    {
                        DeliveredChannels = delivered,
                        FailedAttempts = attempts,
                        FirstAttemptAt = firstAttempt,
                        NextAttemptAt = retryAt,
                        LastError = error,
                    };
                }
            }
            else if (result.Channels.Count == 0 && delivered.Count == 0 && delivery.IsAboutOpenAlert)
            {
                // No channel is configured or available for it yet: the alert stays pending and is
                // offered again at the next opportunity. Other notices are not worth keeping.
                next = delivery with { FirstAttemptAt = firstAttempt, NextAttemptAt = now };
            }
            else
            {
                next = null; // every eligible channel has it
            }

            lock (_gate)
            {
                var deliveries = Deliveries();
                if (!deliveries.ContainsKey(delivery.Id))
                    return; // superseded while it was being sent
                if (next is null)
                {
                    _store.DeleteDelivery(delivery.Id);
                    deliveries.Remove(delivery.Id);
                }
                else
                {
                    _store.UpsertDelivery(next);
                    deliveries[delivery.Id] = next;
                }
            }
        }
        finally
        {
            _inFlight.TryRemove(delivery.Id, out _);
        }
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
        {
            return Deliveries().Values.FirstOrDefault(d =>
                d.IsAlertNotice && string.Equals(d.AlertKey, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RemoveNotices(string key)
    {
        lock (_gate)
            RemoveNoticesLocked(key);
    }

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