namespace WatchTower.Models;

public record DiskMetric(
    DateTime Timestamp,
    string DriveName,
    double TotalGb,
    double FreeGb,
    double UsagePercent
);
