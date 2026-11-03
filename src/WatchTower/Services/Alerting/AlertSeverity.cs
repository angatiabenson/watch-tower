namespace WatchTower.Services.Alerting;

/// <summary>
/// How urgent an alert is. Stored as an integer in <c>alert_state.severity</c> and compared
/// against each notification channel's <c>MinSeverity</c>: append-only, explicit values.
/// </summary>
public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}