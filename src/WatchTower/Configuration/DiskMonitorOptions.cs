namespace WatchTower.Configuration;

public class DiskMonitorOptions
{
    public const string SectionName = "Monitoring:Disk";

    public int PollIntervalSeconds { get; set; } = 60;
    public double WarningThresholdPercent { get; set; } = 70.0;
    public double CriticalThresholdPercent { get; set; } = 85.0;
    public double QueueLengthWarningThreshold { get; set; } = 2.0;
}
