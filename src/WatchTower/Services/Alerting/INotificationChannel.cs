namespace WatchTower.Services.Alerting;

/// <summary>Delivers notifications for one <see cref="NotificationChannelType"/>.</summary>
public interface INotificationChannel
{
    NotificationChannelType Type { get; }

    /// <summary>
    /// Whether <paramref name="channel"/> can deliver at all (e.g. SMTP configured, URL valid).
    /// Unavailable channels are skipped and do not count as notified.
    /// </summary>
    bool IsAvailable(NotificationChannel channel);

    /// <summary>
    /// Sends <paramref name="notification"/>. Returns a failure (never throws for delivery
    /// problems); the message must not contain the channel URL or secret.
    /// </summary>
    Task<ChannelSendResult> SendAsync(NotificationChannel channel, AlertNotification notification, CancellationToken ct);
}

public sealed record ChannelSendResult(bool Success, string Message)
{
    public static ChannelSendResult Ok(string message = "Delivered") => new(true, message);
    public static ChannelSendResult Failed(string message) => new(false, message);
}

/// <summary>Outcome of one channel for a test notification (no URLs or secrets).</summary>
public sealed record ChannelTestResult(string Channel, string Type, bool Ok, string Message);