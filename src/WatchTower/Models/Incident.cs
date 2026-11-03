namespace WatchTower.Models;

public record Incident(
    DateTime Timestamp,
    IncidentType Type,
    string Description,
    List<ProcessInfo>? TopProcesses = null
);