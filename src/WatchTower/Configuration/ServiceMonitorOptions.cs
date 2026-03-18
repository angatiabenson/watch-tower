namespace WatchTower.Configuration;

public class ServiceMonitorOptions
{
    public const string SectionName = "Monitoring:Services";

    public int PollIntervalSeconds { get; set; } = 30;
    public int MaxRestartAttempts { get; set; } = 3;
    public int RestartDelaySeconds { get; set; } = 10;
    public List<string> WatchedServices { get; set; } = new();
}
