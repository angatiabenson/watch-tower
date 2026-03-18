namespace WatchTower.Configuration;

public class RamMonitorOptions
{
    public const string SectionName = "Monitoring:Ram";

    public int PollIntervalSeconds { get; set; } = 5;
    public double ThresholdPercent { get; set; } = 90.0;
    public int SustainedSeconds { get; set; } = 300;
    public int TopProcessCount { get; set; } = 5;
}
