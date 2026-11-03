namespace WatchTower.Services.Alerting;

/// <summary>Delivers alert notifications to the configured channels.</summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Sends <paramref name="notification"/> to every eligible channel and returns how many
    /// channels it was handed to (whether or not delivery succeeded). Zero means no channel is
    /// configured for it, so the alert stays "pending" and is offered again on its next raise.
    /// Never throws for channel failures.
    /// </summary>
    Task<int> DispatchAsync(AlertNotification notification, CancellationToken ct);
}