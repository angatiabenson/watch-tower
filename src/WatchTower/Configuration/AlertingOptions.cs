namespace WatchTower.Configuration;

/// <summary>How alerts are (re-)notified. Section <c>Alerting</c>.</summary>
public class AlertingOptions
{
    public const string SectionName = "Alerting";

    /// <summary>Minutes between reminders while an alert stays open. 0 = notify once per occurrence.</summary>
    public int RenotifyMinutes { get; set; } = 60;

    /// <summary>Send a "resolved" notification (with how long the alert lasted) when it clears.</summary>
    public bool SendRecovery { get; set; } = true;

    /// <summary>Attach the full 24-hour server report to alert emails. Off: a short alert email is sent.</summary>
    public bool IncludeFullReport { get; set; }

    /// <summary>
    /// How long a CPU, memory, disk, disk-queue or network condition must stay clear before its
    /// alert resolves, so readings hovering around a threshold do not flap. 0 = immediately.
    /// </summary>
    public int ResolveAfterSeconds { get; set; } = 300;
}