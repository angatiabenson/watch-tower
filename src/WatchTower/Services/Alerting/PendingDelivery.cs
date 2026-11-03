namespace WatchTower.Services.Alerting;

/// <summary>
/// A notification that has not yet reached every eligible channel (<c>alert_delivery</c> row):
/// held during a maintenance window, or waiting to retry the channels that failed. Channels that
/// already received it are listed in <paramref name="DeliveredChannels"/> and never sent it again.
/// The row is deleted once every eligible channel has it, when it is superseded (a newer
/// notification for the same alert, or the alert resolved) or when retries are given up.
/// </summary>
/// <param name="Id">Unique id of this notification.</param>
/// <param name="Notification">The message, exactly as it is (re)sent.</param>
/// <param name="CreatedAt">When the notification was created (UTC).</param>
/// <param name="DeliveredChannels">Channel names that delivered it (case-insensitive).</param>
/// <param name="FailedAttempts">Attempts in which at least one channel failed.</param>
/// <param name="FirstAttemptAt">First delivery attempt (UTC); null until it is attempted.
/// Retries of a notification that is not about an open alert stop
/// <see cref="NotificationRetryPolicy.GiveUpAfter"/> after it.</param>
/// <param name="NextAttemptAt">Earliest next attempt (UTC).</param>
/// <param name="LastError">Last failure, per channel (no URLs or secrets).</param>
/// <param name="Held">Held back by a maintenance window: not attempted until the window ends.</param>
public sealed record PendingDelivery(
    string Id,
    AlertNotification Notification,
    DateTime CreatedAt,
    IReadOnlyList<string> DeliveredChannels,
    int FailedAttempts,
    DateTime? FirstAttemptAt,
    DateTime NextAttemptAt,
    string? LastError,
    bool Held = false)
{
    public string AlertKey => Notification.AlertKey;

    /// <summary>An opened or reminder notification: about an alert that is (still) open.</summary>
    public bool IsAboutOpenAlert => Notification.Kind is AlertNotificationKind.Opened or AlertNotificationKind.Reminder;

    /// <summary>An opened, reminder or resolved notification (one per alert key at a time).</summary>
    public bool IsAlertNotice => IsAboutOpenAlert || Notification.Kind == AlertNotificationKind.Resolved;

    public static PendingDelivery Create(AlertNotification notification, DateTime now, bool held) =>
        new(Guid.NewGuid().ToString("N"), notification, now, [], 0, null, now, null, held);
}

/// <summary>When failed notification deliveries are retried.</summary>
public static class NotificationRetryPolicy
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);

    /// <summary>Retry delays double from <see cref="FirstDelay"/> up to this.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Retries of a resolved notice or one-shot event stop this long after the first attempt
    /// (logged as an error). Opened and reminder notices are retried for as long as their alert
    /// stays open (they are superseded when it resolves, escalates or the next reminder is due).
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>Delay after the <paramref name="failedAttempts"/>-th failed attempt: 1, 2, 4, 8, 16, then 30 minutes.</summary>
    public static TimeSpan Backoff(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts, 1, 16) - 1;
        var delay = FirstDelay * Math.Pow(2, exponent);
        return delay < MaxDelay ? delay : MaxDelay;
    }
}