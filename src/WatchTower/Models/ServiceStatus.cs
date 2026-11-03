namespace WatchTower.Models;

public record ServiceStatus(
    DateTime Timestamp,
    string ServiceName,
    string DisplayName,
    string Status,
    bool IsRunning
);