namespace WatchTower.Configuration;

public class NetworkMonitorOptions
{
    public const string SectionName = "Monitoring:Network";

    public int PollIntervalSeconds { get; set; } = 30;
    public double OutputQueueWarningThreshold { get; set; } = 0.0;
}
