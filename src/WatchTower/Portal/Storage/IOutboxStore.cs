namespace WatchTower.Portal.Storage;

public interface IOutboxStore
{
    Task AddMessageAsync(string messageType, string payloadJson, int priority = 0);
    Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int limit);
    Task MarkSentAsync(long id);
    Task MarkFailedAsync(long id, string error, DateTime? nextRetryAt);
    Task PurgeDeliveredAsync(TimeSpan maxAge);
}

public record OutboxMessage(
    long Id,
    string MessageType,
    string PayloadJson,
    int Priority,
    int Attempts
);
