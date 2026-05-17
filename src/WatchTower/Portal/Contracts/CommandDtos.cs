namespace WatchTower.Portal.Contracts;

public record AgentCommandDto(
    string CommandId,
    int ActionType,
    Dictionary<string, string> Parameters,
    string? ScheduledAt,
    string IssuedAt
);

public record AcknowledgeCommandDto(string CommandId, string AcknowledgedAt);

public record CommandResultDto(
    string CommandId,
    bool Success,
    string Message,
    string ExecutedAt
);
