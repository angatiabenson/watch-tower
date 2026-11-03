using WatchTower.Models;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Persisted state of one alert key (<c>alert_state</c> row). A key has at most one row: raising
/// a resolved key re-opens it with fresh counters.
/// </summary>
/// <param name="Key">The <see cref="AlertKey"/> value.</param>
/// <param name="Severity">Severity of the open (or last) alert.</param>
/// <param name="IncidentType">Incident type that opened the alert (used for resolved portal events).</param>
/// <param name="Title">Short headline used for notifications, e.g. "Disk Critical: C:\ at 91.0%".</param>
/// <param name="OpenedAt">When the alert opened (UTC).</param>
/// <param name="LastNotifiedAt">When a notification was last handed to a channel (UTC); null while pending.</param>
/// <param name="NotifyCount">Notifications sent for this occurrence (opened + reminders).</param>
/// <param name="ResolvedAt">When the alert resolved (UTC); null while open.</param>
/// <param name="LastDescription">Latest description reported for the condition.</param>
public sealed record AlertState(
    string Key,
    AlertSeverity Severity,
    IncidentType IncidentType,
    string Title,
    DateTime OpenedAt,
    DateTime? LastNotifiedAt,
    int NotifyCount,
    DateTime? ResolvedAt,
    string LastDescription)
{
    public bool IsOpen => ResolvedAt is null;
}