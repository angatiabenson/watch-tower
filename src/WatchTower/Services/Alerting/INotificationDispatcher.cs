namespace WatchTower.Services.Alerting;

/// <summary>Delivers alert notifications to the configured channels.</summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Names of the channels <see cref="DispatchAsync"/> would send <paramref name="notification"/>
    /// to (enabled, available, and its severity meets their <c>MinSeverity</c>). Cheap and
    /// synchronous: the alert manager asks it when a notification is created (its channel
    /// obligations) and while scheduling (to skip obligations the configuration no longer allows).
    /// </summary>
    IReadOnlyList<string> ChannelsFor(AlertNotification notification);

    /// <summary>
    /// Sends <paramref name="notification"/> to the eligible channels named in
    /// <paramref name="channels"/> (every eligible channel when null) and reports the outcome
    /// per channel. No channels in the result means none of them is configured or available.
    /// Never throws for channel failures.
    /// <paramref name="onChannelCompleted"/> is invoked as soon as each channel has its outcome,
    /// before the slower channels finish, and also when <paramref name="ct"/> later cancels the
    /// dispatch (a channel cut off by the cancellation reports nothing), so a caller can record
    /// what was delivered even if the whole dispatch never completes. It must not throw.
    /// </summary>
    Task<DispatchResult> DispatchAsync(
        AlertNotification notification,
        IReadOnlySet<string>? channels,
        Action<ChannelDelivery>? onChannelCompleted,
        CancellationToken ct);
}

/// <summary>Outcome of one channel for one notification. <paramref name="Message"/> has no URLs or secrets.</summary>
public sealed record ChannelDelivery(string Channel, bool Success, string Message);

/// <summary>Per-channel outcome of <see cref="INotificationDispatcher.DispatchAsync"/>.</summary>
public sealed record DispatchResult(IReadOnlyList<ChannelDelivery> Channels)
{
    public static readonly DispatchResult None = new([]);

    public IEnumerable<string> Delivered => Channels.Where(c => c.Success).Select(c => c.Channel);

    public IEnumerable<ChannelDelivery> Failed => Channels.Where(c => !c.Success);
}