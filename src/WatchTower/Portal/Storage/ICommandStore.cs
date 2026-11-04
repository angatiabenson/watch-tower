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
    Task<IReadOnlyList<AgentCommandRecord>> GetPendingCommandsAsync();
    /// <summary>
    /// Atomically transitions a command from Received/Acknowledged to Executing.
    /// Returns <c>true</c> only if this caller won the claim; <c>false</c> if the command
    /// was already claimed/executed (prevents the poll and scheduled paths double-running it).
    /// </summary>
    Task<bool> TryBeginExecutionAsync(string commandId);
    Task MarkCompletedAsync(string commandId, CommandResultDto result);
    Task MarkFailedAsync(string commandId, string error);
    Task<IReadOnlyList<AgentCommandRecord>> GetUnsentResultsAsync();
    Task MarkResultSentAsync(string commandId);
    Task<IReadOnlyList<AgentCommandRecord>> GetDueScheduledCommandsAsync();

    /// <summary>
    /// Marks every command still in 'Executing' (left behind by a previous process) as
    /// 'Failed' with <paramref name="message"/> and returns the stored (unsent) results.
    /// </summary>
    Task<IReadOnlyList<CommandResultDto>> FailInterruptedCommandsAsync(string message);

    /// <summary>
    /// Deletes 'Completed'/'Failed' commands that finished before <paramref name="olderThanUtc"/>.
    /// Returns the number of rows deleted.
    /// </summary>
    Task<int> PurgeFinishedCommandsAsync(DateTime olderThanUtc);
}

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