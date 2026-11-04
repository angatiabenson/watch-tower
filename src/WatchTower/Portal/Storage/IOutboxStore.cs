namespace WatchTower.Portal.Storage;

public interface IOutboxStore
{
    /// <param name="cpuRowId">Highest cpu_metrics rowid covered by this message (for MetricsBatch), else null.</param>
    /// <param name="ramRowId">Highest ram_metrics rowid covered by this message (for MetricsBatch), else null.</param>
    Task AddMessageAsync(string messageType, string payloadJson, int priority = 0, long? cpuRowId = null, long? ramRowId = null);
    Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int limit);
    Task MarkSentAsync(long id);
    /// <summary>Records a failed delivery. Transitions the message to terminal 'Failed' once attempts reach <paramref name="maxAttempts"/>.</summary>
    Task MarkFailedAsync(long id, string error, DateTime? nextRetryAt, int maxAttempts);
    /// <summary>Gives up on a message without another attempt (terminal 'Failed'), recording why.</summary>
    Task MarkDroppedAsync(long id, string reason);
    Task PurgeDeliveredAsync(TimeSpan maxAge);
}

/// <param name="CreatedAt">When the message was enqueued (UTC); null if unknown.</param>
public record OutboxMessage(
    long Id,
    string MessageType,
    string PayloadJson,
    int Priority,
    int Attempts,
    long? CpuRowId = null,
    long? RamRowId = null,
    DateTime? CreatedAt = null
);