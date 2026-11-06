namespace WatchTower.Services.Alerting;

/// <summary>
/// A condition that is present but still inside its waiting period (for example
/// <c>Monitoring:Disk:QueueSustainedSeconds</c>): it is shown in the console and reported to
/// the portal, but nothing has been raised or sent yet. It becomes an alert at
/// <see cref="AlertsAt"/> if it is still present then; if it clears first, nobody is notified.
/// </summary>
/// <param name="Key">The <see cref="AlertKey"/> value the alert will have, e.g. <c>diskqueue:0 C:</c>.</param>
/// <param name="Severity">Severity the alert will have.</param>
/// <param name="Title">Latest headline reported for the condition, e.g. "Disk Queue High: 0 C: at 3.4".</param>
/// <param name="Since">When the condition was first seen at this severity (UTC).</param>
/// <param name="AlertAfter">The waiting period.</param>
/// <param name="LastSeenAt">When the condition was last reported present (UTC).</param>
public sealed record PendingAlert(
    string Key,
    AlertSeverity Severity,
    string Title,
    DateTime Since,
    TimeSpan AlertAfter,
    DateTime LastSeenAt)
{
    /// <summary>When the alert is raised if the condition is still present (UTC).</summary>
    public DateTime AlertsAt => Since + AlertAfter;
}

/// <summary>
/// Breaches of one kind (<see cref="AlertKinds"/>) that cleared within their waiting period, so
/// nobody was notified: how many, and the longest one.
/// </summary>
public sealed record ShortBreachSummary(string Kind, string Label, int Count, TimeSpan Longest);

/// <summary>Alert kinds by key prefix, with the label operators see.</summary>
public static class AlertKinds
{
    /// <summary>Kind of an alert key: <c>cpu</c>, <c>ram</c>, or the prefix without its colon (<c>disk</c>, <c>diskqueue</c>, <c>net</c>, <c>svc</c>, <c>pool</c>, <c>endpoint</c>).</summary>
    public static string Of(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return (colon < 0 ? key : key[..colon]).ToLowerInvariant();
    }

    public static string Label(string kind) => kind switch
    {
        "cpu" => "CPU",
        "ram" => "Memory",
        "disk" => "Disk space",
        "diskqueue" => "Disk queue",
        "net" => "Network queue",
        "svc" => "Service down",
        "pool" => "App pool down",
        "endpoint" => "Endpoint down",
        _ => kind,
    };
}