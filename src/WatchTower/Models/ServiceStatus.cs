namespace WatchTower.Models;

/// <param name="StartType">
/// The service's configured start mode (e.g. <c>Automatic</c>, <c>Manual</c>, <c>Disabled</c>),
/// or <c>null</c> when unknown (not queried, or loaded from storage).
/// </param>
public record ServiceStatus(
    DateTime Timestamp,
    string ServiceName,
    string DisplayName,
    string Status,
    bool IsRunning,
    string? StartType = null
);