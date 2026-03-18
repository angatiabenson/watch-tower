namespace WatchTower.Configuration;

public class CpuMonitorOptions
{
    public const string SectionName = "Monitoring:Cpu";

    public int PollIntervalSeconds { get; set; } = 5;
    public double ThresholdPercent { get; set; } = 99.0;
    public int SustainedSeconds { get; set; } = 300;
    public int TopProcessCount { get; set; } = 3;
}
