using WatchTower.Models;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Owns the open/resolved lifecycle of every alert and decides when operators are notified.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Waiting periods: monitors report a breach with <see cref="ObserveBreach"/> and raise it
/// only once it lasted its alert's <c>SustainedSeconds</c>; until then it is pending (see
/// <see cref="PendingAlert"/>), and one that clears first is never notified.</item>
/// <item>First raise of a key:the incident is recorded and published to the portal, and a
/// notification is sent.</item>
/// <item>Raising an open key: nothing is recorded; a reminder is sent only every
/// <c>Alerting:RenotifyMinutes</c> (0 = never). Raising at a higher severity escalates
/// (recorded and notified like a new alert).</item>
/// <item>Resolving an open key: a "resolved" event goes to the portal and, if
/// <c>Alerting:SendRecovery</c> and the alert had been notified, a resolved notification with
/// the alert's duration is sent.</item>
/// <item>State is persisted, so an alert that was open before a restart is not re-sent.</item>
/// <item>Every notification is persisted as a <see cref="PendingDelivery"/> until each channel it
/// is owed to (the channels configured for its severity when it was created) has it. Every send,
/// immediate or retried, goes through one per-channel scheduler: ordered per alert, then by
/// priority (with aging), then oldest first. Channels that failed are retried with
/// <see cref="NotificationRetryPolicy"/>
/// (also after a restart, see <see cref="ProcessPendingAsync"/>); channels that delivered are
/// never sent it again. An alert counts as notified (<c>NotifyCount</c>, <c>LastNotifiedAt</c>)
/// only once a notice reached at least one channel.</item>
/// </list>
/// </remarks>
public interface IAlertManager
{
    /// <summary>
    /// How long a flapping metric condition must stay clear before its alert resolves
    /// (<c>Alerting:ResolveAfterSeconds</c>); see <see cref="RecoveryDebouncer"/>.
    /// </summary>
    TimeSpan RecoveryWindow { get; }

    /// <summary>
    /// Reports that the condition behind <paramref name="key"/> is present.
    /// <paramref name="incidentFactory"/> is only invoked when something will be recorded or
    /// sent (new alert, escalation or reminder), so callers can defer expensive detail such as
    /// top-process sampling.
    /// </summary>
    Task RaiseAsync(
        AlertKey key,
        AlertSeverity severity,
        string title,
        Func<CancellationToken, Task<Incident>> incidentFactory,
        CancellationToken ct = default);

    /// <summary>
    /// Reports that the condition behind <paramref name="key"/> has cleared. Returns true when
    /// an open alert was resolved. <paramref name="recoveryIncident"/> (e.g. ServiceRestarted)
    /// is recorded and published only in that case.
    /// </summary>
    Task<bool> ResolveAsync(AlertKey key, Incident? recoveryIncident = null, CancellationToken ct = default);

    /// <summary>
    /// Retires every open alert whose key starts with <paramref name="keyPrefix"/> (e.g.
    /// <see cref="AlertKey.ServicePrefix"/>) but is not in <paramref name="configured"/>: what it
    /// watched was removed from the configuration, so it can never resolve on its own. Each is
    /// closed with <paramref name="reason"/> as its <see cref="AlertState.RetiredReason"/>, a
    /// resolved portal event and (if it had been notified) a resolved notice that says why.
    /// Returns the retired keys.
    /// </summary>
    Task<IReadOnlyList<string>> RetireUnconfiguredAsync(
        string keyPrefix, IEnumerable<AlertKey> configured, string reason, CancellationToken ct = default);

    /// <summary>
    /// Retires the open alert <paramref name="key"/> with <paramref name="reason"/>, like
    /// <see cref="RetireUnconfiguredAsync"/>: for resources that are discovered rather than
    /// configured (disks, network adapters) and have disappeared (see
    /// <see cref="AbsentResourceTracker"/>). Returns true when an open alert was retired.
    /// </summary>
    Task<bool> RetireAsync(AlertKey key, string reason, CancellationToken ct = default);

    /// <summary>
    /// Records, publishes and notifies a one-shot event that has no open/resolved lifecycle
    /// (e.g. "server restarted"). Not stored in <c>alert_state</c>.
    /// </summary>
    Task NotifyEventAsync(
        AlertKey key, AlertSeverity severity, string title, Incident incident, CancellationToken ct = default);

    /// <summary>
    /// Waiting period: reports that the condition behind <paramref name="key"/> is present at
    /// <paramref name="severity"/> and says whether to raise it now. True when its alert is
    /// already open at this severity or higher (keep it open), or when the condition has been
    /// present at this severity for <paramref name="alertAfter"/> (the per-alert
    /// <c>SustainedSeconds</c> setting; zero = at once). Until then it is a
    /// <see cref="PendingAlert"/>: shown in the console and reported to the portal, but nothing
    /// is recorded as an alert or sent. Raising the key at this severity ends the wait (see
    /// <see cref="RaiseAsync"/>). Monitors call <see cref="ClearBreach"/> for every reading in
    /// which the condition is absent.
    /// </summary>
    bool ObserveBreach(AlertKey key, AlertSeverity severity, TimeSpan alertAfter, string title);

    /// <summary>
    /// The condition behind <paramref name="key"/> is absent: a breach still inside its waiting
    /// period is dropped silently (nobody was told, so no "resolved" either) and counted in the
    /// daily report. An open alert is unaffected; it resolves through <see cref="ResolveAsync"/>
    /// as before.
    /// </summary>
    void ClearBreach(AlertKey key);

    /// <summary>Breaches still inside their waiting period, oldest first.</summary>
    IReadOnlyList<PendingAlert> GetPendingAlerts();

    /// <summary>Breaches that cleared within their waiting period since <paramref name="sinceUtc"/>, per kind.</summary>
    IReadOnlyList<ShortBreachSummary> GetShortBreaches(DateTime sinceUtc);

    bool IsOpen(AlertKey key);

    /// <summary>Currently open alerts, oldest first.</summary>
    IReadOnlyList<AlertState> GetOpenAlerts();

    /// <summary>Notifications not yet delivered to every channel they are owed to (held, queued or retrying), oldest first.</summary>
    IReadOnlyList<PendingDelivery> GetPendingDeliveries();

    /// <summary>
    /// Outside a maintenance window: sends what the last window held back and retries the
    /// deliveries whose backoff has elapsed. Held notices for alerts that are still open, and
    /// resolutions of alerts notified before the window, are sent one by one; alerts that opened
    /// and cleared inside the window and held one-shot events are sent as one summary.
    /// Maintenance is checked before every send: if a new window starts while a batch is being
    /// sent, the rest stays pending until that window ends.
    /// Called periodically by <c>NotificationDeliveryWorker</c>.
    /// </summary>
    Task ProcessPendingAsync(CancellationToken ct = default);
}

public static class AlertManagerExtensions
{
    public static Task RaiseAsync(
        this IAlertManager alerts, AlertKey key, AlertSeverity severity, string title, Incident incident,
        CancellationToken ct = default) =>
        alerts.RaiseAsync(key, severity, title, _ => Task.FromResult(incident), ct);

    /// <summary>
    /// Raises <paramref name="key"/> once its waiting period is over (see
    /// <see cref="IAlertManager.ObserveBreach"/>). Returns true when it was raised (or kept open).
    /// </summary>
    public static async Task<bool> RaiseAfterAsync(
        this IAlertManager alerts, AlertKey key, AlertSeverity severity, TimeSpan alertAfter, string title,
        Func<CancellationToken, Task<Incident>> incidentFactory, CancellationToken ct = default)
    {
        if (!alerts.ObserveBreach(key, severity, alertAfter, title))
            return false;
        await alerts.RaiseAsync(key, severity, title, incidentFactory, ct);
        return true;
    }

    /// <inheritdoc cref="RaiseAfterAsync(IAlertManager, AlertKey, AlertSeverity, TimeSpan, string, Func{CancellationToken, Task{Incident}}, CancellationToken)"/>
    public static Task<bool> RaiseAfterAsync(
        this IAlertManager alerts, AlertKey key, AlertSeverity severity, TimeSpan alertAfter, string title,
        Incident incident, CancellationToken ct = default) =>
        alerts.RaiseAfterAsync(key, severity, alertAfter, title, _ => Task.FromResult(incident), ct);
}