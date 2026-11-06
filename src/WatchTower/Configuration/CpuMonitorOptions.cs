namespace WatchTower.Configuration;

public class CpuMonitorOptions
{
    public const string SectionName = "Monitoring:Cpu";

    public int PollIntervalSeconds { get; set; } = 5;
    public double ThresholdPercent { get; set; } = 99.0;
    /// <summary>
    /// Waiting period: usage must stay at or above the threshold this long before anyone is
    /// notified. A reading under the threshold starts it over. 0 = alert on the first reading.
    /// </summary>
    public int SustainedSeconds { get; set; } = 300;
    public int TopProcessCount { get; set; } = 3;
}