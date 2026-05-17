using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Storage;

public interface ICommandStore
{
    Task SaveCommandAsync(AgentCommandDto command);
    Task<IReadOnlyList<AgentCommandRecord>> GetPendingCommandsAsync();
    Task MarkExecutingAsync(string commandId);
    Task MarkCompletedAsync(string commandId, CommandResultDto result);
    Task MarkFailedAsync(string commandId, string error);
    Task<IReadOnlyList<AgentCommandRecord>> GetUnsentResultsAsync();
    Task MarkResultSentAsync(string commandId);
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
