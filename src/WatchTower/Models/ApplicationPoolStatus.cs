namespace WatchTower.Models;

public record ApplicationPoolStatus(
    DateTime Timestamp,
    string ApplicationPoolName,
    string Status,
    bool IsRunning
);