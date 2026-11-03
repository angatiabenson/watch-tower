namespace WatchTower.Portal.Contracts;

public record AgentEventDto(
    string EventId,
    string Timestamp,
    int IncidentType,
    string Description,
    List<ProcessInfoDto>? TopProcesses
);

public record ProcessInfoDto(
    string Name,
    int Pid,
    double CpuSeconds,
    double MemoryMb,
    double CpuPercent
);