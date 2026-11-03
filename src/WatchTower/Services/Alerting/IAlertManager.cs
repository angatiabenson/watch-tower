using WatchTower.Models;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Owns the open/resolved lifecycle of every alert and decides when operators are notified.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>First raise of a key: the incident is recorded and published to the portal, and a
/// notification is sent.</item>
/// <item>Raising an open key: nothing is recorded; a reminder is sent only every
/// <c>Alerting:RenotifyMinutes</c> (0 = never). Raising at a higher severity escalates
/// (recorded and notified like a new alert).</item>
/// <item>Resolving an open key: a "resolved" event goes to the portal and, if
/// <c>Alerting:SendRecovery</c> and the alert had been notified, a resolved notification with
/// the alert's duration is sent.</item>
/// <item>State is persisted, so an alert that was open before a restart is not re-sent.</item>
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
    /// Records, publishes and notifies a one-shot event that has no open/resolved lifecycle
    /// (e.g. "server restarted"). Not stored in <c>alert_state</c>.
    /// </summary>
    Task NotifyEventAsync(
        AlertKey key, AlertSeverity severity, string title, Incident incident, CancellationToken ct = default);

    bool IsOpen(AlertKey key);

    /// <summary>Currently open alerts, oldest first.</summary>
    IReadOnlyList<AlertState> GetOpenAlerts();
}

public static class AlertManagerExtensions
{
    public static Task RaiseAsync(
        this IAlertManager alerts, AlertKey key, AlertSeverity severity, string title, Incident incident,
        CancellationToken ct = default) =>
        alerts.RaiseAsync(key, severity, title, _ => Task.FromResult(incident), ct);
}