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
    private readonly string _server;

    private readonly Lock _gate = new();
    private Dictionary<string, AlertState>? _states;

    public AlertManager(
        IAlertStateStore store,
        IMetricsStore metricsStore,
        IPortalEventPublisher portal,
        INotificationDispatcher dispatcher,
        IOptionsMonitor<AlertingOptions> options,
        TimeProvider time,
        ILogger<AlertManager> logger,
        string? serverName = null)
    {
        _store = store;
        _metricsStore = metricsStore;
        _portal = portal;
        _dispatcher = dispatcher;
        _options = options;
        _time = time;
        _logger = logger;
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

        if (open && !escalated && !IsReminderDue(existing!, now, options))
            return; // already open and notified: stay quiet

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

        await NotifyAsync(state, notification, ct);
    }

    public async Task<bool> ResolveAsync(AlertKey key, Incident? recoveryIncident = null, CancellationToken ct = default)
    {
        var existing = TryGet(key);
        if (existing is not { IsOpen: true })
            return false;

        var now = UtcNow;
        var duration = now - existing.OpenedAt;
        Save(existing with { ResolvedAt = now });

        string description;
        if (recoveryIncident is not null)
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

        _logger.LogInformation("Alert {Key} resolved after {Duration}", key.Value, AlertText.FormatDuration(duration));

        var options = _options.CurrentValue;
        if (options.SendRecovery && existing.NotifyCount > 0)
        {
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
                NotifyCount: existing.NotifyCount);
            await DispatchSafelyAsync(notification, ct);
        }

        return true;
    }

    public async Task NotifyEventAsync(
        AlertKey key, AlertSeverity severity, string title, Incident incident, CancellationToken ct = default)
    {
        _metricsStore.AddIncident(incident);
        await _portal.PublishAlertEventAsync(incident, key.Value, resolved: null, ct);
        _logger.LogInformation("Event {Key}: {Description}", key.Value, incident.Description);

        await DispatchSafelyAsync(new AlertNotification(
            AlertNotificationKind.Event, key.Value, severity, title, incident.Description, _server,
            incident.Timestamp, Incident: incident), ct);
    }

    private static bool IsReminderDue(AlertState state, DateTime now, AlertingOptions options)
    {
        // Never notified (no channel was configured yet): offer it again.
        if (state.LastNotifiedAt is not { } last)
            return true;
        return options.RenotifyMinutes > 0 && now - last >= TimeSpan.FromMinutes(options.RenotifyMinutes);
    }

    private async Task NotifyAsync(AlertState state, AlertNotification notification, CancellationToken ct)
    {
        var handedTo = await DispatchSafelyAsync(notification, ct);
        if (handedTo == 0)
            return; // stays pending

        // Counted as notified whether or not delivery succeeded, so a failing channel cannot turn
        // every poll into another send attempt (it is retried at the next reminder).
        Save(state with { LastNotifiedAt = notification.OccurredAtUtc, NotifyCount = state.NotifyCount + 1 });
    }

    private async Task<int> DispatchSafelyAsync(AlertNotification notification, CancellationToken ct)
    {
        try
        {
            return await _dispatcher.DispatchAsync(notification, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Dispatching the {Kind} notification for alert {Key} failed",
                notification.Kind, notification.AlertKey);
            return 0;
        }
    }

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