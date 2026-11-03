using WatchTower.Models;

namespace WatchTower.Services.Alerting;

/// <summary>Why a notification is being sent.</summary>
public enum AlertNotificationKind
{
    /// <summary>An alert opened (or escalated to a higher severity).</summary>
    Opened,

    /// <summary>An alert is still open after <c>Alerting:RenotifyMinutes</c>.</summary>
    Reminder,

    /// <summary>An open alert cleared.</summary>
    Resolved,

    /// <summary>A one-shot event that has no open/resolved lifecycle (e.g. a server restart).</summary>
    Event,

    /// <summary>A test message requested by an operator.</summary>
    Test,
}

/// <summary>One message to deliver through the notification channels.</summary>
/// <param name="Kind">Opened, reminder, resolved, one-shot event or test.</param>
/// <param name="AlertKey">The <see cref="Alerting.AlertKey"/> value (e.g. <c>disk:C:\</c>).</param>
/// <param name="Severity">Severity used for channel filtering.</param>
/// <param name="Title">Headline, e.g. "CPU Emergency: 99.5%".</param>
/// <param name="Description">Details of the condition (or of its recovery).</param>
/// <param name="Server">Machine name of this server.</param>
/// <param name="OccurredAtUtc">When this notification's event happened.</param>
/// <param name="OpenedAtUtc">When the alert opened (reminders and resolutions).</param>
/// <param name="Duration">How long the alert has been (or was) open.</param>
/// <param name="Incident">The incident behind the notification, when there is one.</param>
/// <param name="NotifyCount">How many notifications this occurrence has produced, including this one.</param>
public sealed record AlertNotification(
    AlertNotificationKind Kind,
    string AlertKey,
    AlertSeverity Severity,
    string Title,
    string Description,
    string Server,
    DateTime OccurredAtUtc,
    DateTime? OpenedAtUtc = null,
    TimeSpan? Duration = null,
    Incident? Incident = null,
    int NotifyCount = 1);