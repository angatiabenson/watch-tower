namespace WatchTower.Configuration;

public class ServiceMonitorOptions
{
    public const string SectionName = "Monitoring:Services";

    public int PollIntervalSeconds { get; set; } = 30;
    public int MaxRestartAttempts { get; set; } = 3;
    public int RestartDelaySeconds { get; set; } = 10;
    public int RestartTimeoutSeconds { get; set; } = 30;

    /// <summary>After a restart, wait this long and re-check the service is still running
    /// before declaring success. Catches crash-looping services that come up then die.</summary>
    public int StableStateDelaySeconds { get; set; } = 5;

    public List<string> WatchedServices { get; set; } = new();
}