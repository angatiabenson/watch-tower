namespace WatchTower.Configuration;

public class NetworkMonitorOptions
{
    public const string SectionName = "Monitoring:Network";

    public int PollIntervalSeconds { get; set; } = 30;
    public double OutputQueueWarningThreshold { get; set; } = 0.0;

    /// <summary>
    /// Waiting period: an adapter's output queue must stay above the threshold this long before
    /// anyone is notified. 0 = alert on the first reading.
    /// </summary>
    public int SustainedSeconds { get; set; } = 300;
}