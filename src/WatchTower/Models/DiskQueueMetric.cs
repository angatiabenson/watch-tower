namespace WatchTower.Models;

public record DiskQueueMetric(
    DateTime Timestamp,
    string InstanceName,
    double QueueLength);
