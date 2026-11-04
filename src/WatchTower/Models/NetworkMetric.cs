namespace WatchTower.Models;

public record NetworkMetric(DateTime Timestamp, string InterfaceName, double OutputQueueLength);