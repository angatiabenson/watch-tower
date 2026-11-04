using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Storage;

public interface ICommandStore
{
    /// <summary>
    /// Records the command id as seen and, if it is new, stores the command as 'Received', in
    /// one transaction, before anything executes it. A known id (see <see cref="PruneCommandIdsAsync"/>)
    /// is never stored again, even after its command row has been purged. Returns <c>true</c>
    /// while the command is waiting to be executed (new, or a re-delivery of one not claimed
    /// yet); <c>false</c> when it is executing, finished, rejected or only remembered by id.
    /// </summary>
    Task<bool> SaveCommandAsync(AgentCommandDto command);

    /// <summary>
    /// Stores a command that is refused at receipt (for example a stale <c>issuedAt</c>) directly
    /// as 'Failed' with an unsent result carrying <paramref name="message"/>, so the result is
    /// reported and retried like any other. Returns <c>false</c> without changing anything when
    /// the command id is already known: a re-delivery must not overwrite the earlier outcome.
    /// </summary>
    Task<bool> TryRecordRejectedAsync(AgentCommandDto command, string message);

    /// <summary>
    /// Forgets remembered command ids first seen before <paramref name="seenBeforeUtc"/> (null:
    /// no age limit), then the oldest ones beyond <paramref name="maxCount"/> (0 or less: no
    /// limit). Ids whose command row still exists are kept. Returns the number forgotten.
    /// </summary>
    Task<int> PruneCommandIdsAsync(DateTime? seenBeforeUtc, int maxCount);

    /// <summary>
    /// Commands stored but not claimed for execution yet ('Received'/'Acknowledged'), oldest
    /// first: deferred ones, and ones a previous process saved but stopped before claiming.
    /// </summary>
    Task<IReadOnlyList<AgentCommandRecord>> GetPendingCommandsAsync();

    /// <summary>
    /// Refuses a stored command that was never claimed: it becomes 'Failed' and rejected (its id
    /// outcome 'Rejected'), with an unsent result carrying <paramref name="message"/> that is
    /// reported and retried like any other rejection. Returns that result, or <c>null</c> without
    /// changing anything when the command is no longer waiting (claimed, finished or unknown).
    /// </summary>
    Task<CommandResultDto?> TryRejectUnclaimedAsync(string commandId, string message);
    /// <summary>
    /// Atomically transitions a command from Received/Acknowledged to Executing.
    /// Returns <c>true</c> only if this caller won the claim; <c>false</c> if the command
    /// was already claimed/executed (prevents the poll and scheduled paths double-running it).
    /// </summary>
    Task<bool> TryBeginExecutionAsync(string commandId);
    Task MarkCompletedAsync(string commandId, CommandResultDto result);
    Task MarkFailedAsync(string commandId, string error);

    /// <summary>Every result waiting for delivery (not delivered, not abandoned), due or not.</summary>
    Task<IReadOnlyList<AgentCommandRecord>> GetUnsentResultsAsync();

    /// <summary>
    /// At most <paramref name="limit"/> results waiting for delivery whose next attempt is due at
    /// <paramref name="nowUtc"/>, the longest-due first (not the oldest: a result the portal
    /// keeps refusing waits out its backoff instead of blocking the others).
    /// </summary>
    Task<IReadOnlyList<AgentCommandRecord>> GetDueResultsAsync(DateTime nowUtc, int limit);

    /// <summary>
    /// Counts a failed attempt to deliver the result of <paramref name="commandId"/> and schedules
    /// the next one after a backoff that doubles from 1 to at most 30 minutes. Returns when the
    /// next attempt is due, or <c>null</c> if no result is waiting.
    /// </summary>
    Task<DateTime?> RecordResultAttemptFailedAsync(string commandId, DateTime nowUtc);

    /// <summary>
    /// Gives up on results still undelivered that were produced before <paramref name="producedBeforeUtc"/>:
    /// they are no longer retried and their rows become eligible for <see cref="PurgeFinishedCommandsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<UndeliveredResult>> AbandonUndeliveredResultsAsync(DateTime producedBeforeUtc, DateTime nowUtc);

    /// <summary>
    /// Deletes the oldest rejected commands beyond the newest <paramref name="keep"/>, whether
    /// their result was delivered or not (their id stays remembered).
    /// </summary>
    Task<TrimmedRejections> TrimRejectionsAsync(int keep);

    Task MarkResultSentAsync(string commandId);
    Task<IReadOnlyList<AgentCommandRecord>> GetDueScheduledCommandsAsync();

    /// <summary>
    /// Marks every command still in 'Executing' (left behind by a previous process) as
    /// 'Failed' with <paramref name="message"/> and returns the stored (unsent) results.
    /// </summary>
    Task<IReadOnlyList<CommandResultDto>> FailInterruptedCommandsAsync(string message);

    /// <summary>
    /// Deletes 'Completed'/'Failed' commands that finished before <paramref name="olderThanUtc"/>
    /// and whose result was delivered or abandoned. Returns the number of rows deleted.
    /// </summary>
    Task<int> PurgeFinishedCommandsAsync(DateTime olderThanUtc);
}

/// <summary>A command result that was never delivered.</summary>
/// <param name="Attempts">Failed delivery attempts so far.</param>
/// <param name="ProducedAt">When the result was produced (UTC, round-trip format).</param>
public record UndeliveredResult(string CommandId, int Attempts, string ProducedAt);

/// <summary>Outcome of <see cref="ICommandStore.TrimRejectionsAsync"/>.</summary>
/// <param name="Deleted">Rejected commands deleted.</param>
/// <param name="Undelivered">Those of them whose result had not been delivered.</param>
public record TrimmedRejections(int Deleted, IReadOnlyList<UndeliveredResult> Undelivered);

public record AgentCommandRecord(
    string CommandId,
    int ActionType,
    string ParamsJson,
    string Status,
    string ReceivedAt,
    string? ScheduledAt,
    string? IssuedAt,
    string? ResultJson
);