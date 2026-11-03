using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Storage;

public interface ICommandStore
{
    Task SaveCommandAsync(AgentCommandDto command);
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