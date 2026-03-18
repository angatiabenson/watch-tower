namespace WatchTower.Models;

public record ServiceStatus(
    DateTime Timestamp,
    string ServiceName,
    string Status,
    bool IsRunning);
