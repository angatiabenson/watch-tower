namespace WatchTower.Models;

public record RamMetric(DateTime Timestamp, double UsagePercent, double AvailableMb, double TotalMb);
