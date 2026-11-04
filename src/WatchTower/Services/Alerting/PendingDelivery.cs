namespace WatchTower.Services.Alerting;

/// <summary>
/// A notification that has not yet reached every eligible channel (<c>alert_delivery</c> row):
/// held during a maintenance window, or waiting to retry the channels that failed. Channels that
/// already received it are listed in <paramref name="DeliveredChannels"/> and never sent it again.
/// The row is deleted once every eligible channel has it, when it is superseded (a newer notice
/// of the same alert occurrence, e.g. its recovery, or a flapping notice it was folded into) or
/// when retries are given up.
/// <para>
/// An alert's notices (opened, reminder, resolved) form an ordered queue per alert key, sorted by
/// <paramref name="Occurrence"/> then <paramref name="Sequence"/>: a new occurrence of the alert
/// never replaces an earlier occurrence's recovery, it queues behind it.
/// </para>
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
/// <param name="ExplicitRecovery">Resolved notices only: the alert's opening may have reached
/// operators (it was notified, or its opened notice was being sent or had been attempted when the
/// alert closed), so this recovery is always sent on its own, never folded into the
/// end-of-maintenance summary.</param>
/// <param name="Occurrence">Alert notices only: the alert occurrence it is about
/// (<see cref="AlertState.Occurrence"/>); 0 for events and summaries.</param>
/// <param name="Sequence">Creation order of pending deliveries (increasing, persisted), which
/// orders an alert's notices within the same occurrence and the delivery worker's batches.</param>
/// <param name="FlappingCount">A flapping notice: how many occurrences of the alert were folded
/// into it (see <see cref="AlertManager.MaxQueuedNotices"/>); 0 for every other notice.</param>
public sealed record PendingDelivery(
    string Id,
    AlertNotification Notification,
    DateTime CreatedAt,
    IReadOnlyList<string> DeliveredChannels,
    int FailedAttempts,
    DateTime? FirstAttemptAt,
    DateTime NextAttemptAt,
    string? LastError,
    bool Held = false,
    bool ExplicitRecovery = false,
    long Occurrence = 0,
    long Sequence = 0,
    int FlappingCount = 0)
{
    public string AlertKey => Notification.AlertKey;

    /// <summary>An opened or reminder notification: about an alert that is (still) open.</summary>
    public bool IsAboutOpenAlert => Notification.Kind is AlertNotificationKind.Opened or AlertNotificationKind.Reminder;

    /// <summary>
    /// An opened, reminder or resolved notification (a flapping notice is a resolved one): queued
    /// per alert key and sent one at a time, in <see cref="QueueOrder"/>.
    /// </summary>
    public bool IsAlertNotice => IsAboutOpenAlert || Notification.Kind == AlertNotificationKind.Resolved;

    /// <summary>A notice standing for several occurrences of a flapping alert.</summary>
    public bool IsFlapping => FlappingCount > 0;

    /// <summary>
    /// A resolved notice of an alert that operators were never told about (opened and cleared
    /// inside a maintenance window): listed in the end-of-window summary instead of sent alone.
    /// </summary>
    public bool IsSummarisedRecovery =>
        Notification is { Kind: AlertNotificationKind.Resolved, NotifyCount: 0 } && !ExplicitRecovery;

    /// <summary>Position in its alert key's queue: occurrence first, then creation order.</summary>
    public (long Occurrence, long Sequence) QueueOrder => (Occurrence, Sequence);

    public static PendingDelivery Create(
        AlertNotification notification, DateTime now, bool held, bool explicitRecovery = false,
        long occurrence = 0, long sequence = 0) =>
        new(Guid.NewGuid().ToString("N"), notification, now, [], 0, null, now, null, held, explicitRecovery,
            occurrence, sequence);
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