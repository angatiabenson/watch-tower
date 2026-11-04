namespace WatchTower.Configuration;

public class ApplicationPoolMonitorOptions
{
    public const string SectionName = "Monitoring:ApplicationPools";

    public int PollIntervalSeconds { get; set; } = 30;
    public int MaxStartAttempts { get; set; } = 3;
    public int StartDelaySeconds { get; set; } = 10;
    public int StartTimeoutSeconds { get; set; } = 30;
    public int StableStateDelaySeconds { get; set; } = 5;
    public List<string> WatchedApplicationPools { get; set; } = new();
}