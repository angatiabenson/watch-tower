namespace WatchTower.Portal.Contracts;

/// <param name="AlertKey">
/// Alert the event belongs to (e.g. <c>cpu</c>, <c>disk:C:\</c>, <c>svc:W3SVC</c>); omitted for
/// incidents outside an alert lifecycle (spikes, service-down notices) and by older agents.
/// </param>
/// <param name="Resolved">
/// <c>false</c>: the alert opened (or escalated). <c>true</c>: the alert cleared; the event's
/// <c>IncidentType</c> is then the recovery type when one exists (ServiceRestarted,
/// ApplicationPoolStarted, EndpointRecovered), otherwise the type that opened the alert.
/// Omitted for one-shot events (e.g. ServerRestarted) and incidents without an alert.
/// </param>
public record AgentEventDto(
    string EventId,
    string Timestamp,
    int IncidentType,
    string Description,
    List<ProcessInfoDto>? TopProcesses,
    string? AlertKey = null,
    bool? Resolved = null
);

public record ProcessInfoDto(
    string Name,
    int Pid,
    double CpuSeconds,
    double MemoryMb,
    double CpuPercent
);