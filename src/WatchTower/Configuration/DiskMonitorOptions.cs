namespace WatchTower.Configuration;

public class DiskMonitorOptions
{
    public const string SectionName = "Monitoring:Disk";

    public int PollIntervalSeconds { get; set; } = 60;
    public double WarningThresholdPercent { get; set; } = 70.0;
    public double CriticalThresholdPercent { get; set; } = 85.0;
    public double QueueLengthWarningThreshold { get; set; } = 2.0;

    /// <summary>
    /// Waiting period for disk space alerts: a drive must stay at or above the critical threshold
    /// this long before anyone is notified. 0 = alert on the first reading.
    /// </summary>
    public int SustainedSeconds { get; set; } = 600;

    /// <summary>
    /// Waiting period for disk queue alerts: the queue must stay above
    /// <see cref="QueueLengthWarningThreshold"/> this long before anyone is notified. 0 = at once.
    /// </summary>
    public int QueueSustainedSeconds { get; set; } = 300;
}