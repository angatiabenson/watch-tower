namespace WatchTower.Services.Alerting;

/// <summary>Delivers alert notifications to the configured channels.</summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Sends <paramref name="notification"/> to every eligible channel except those named in
    /// <paramref name="skipChannels"/> (channels that already received it) and reports the outcome
    /// per channel. No channels in the result means none is configured or available for it.
    /// Never throws for channel failures.
    /// <paramref name="onChannelCompleted"/> is invoked as soon as each channel has its outcome,
    /// before the slower channels finish, and also when <paramref name="ct"/> later cancels the
    /// dispatch (a channel cut off by the cancellation reports nothing), so a caller can record
    /// what was delivered even if the whole dispatch never completes. It must not throw.
    /// </summary>
    Task<DispatchResult> DispatchAsync(
        AlertNotification notification,
        IReadOnlySet<string>? skipChannels,
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