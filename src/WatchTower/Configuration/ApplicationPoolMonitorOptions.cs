namespace WatchTower.Configuration;

public class ApplicationPoolMonitorOptions
{
    public const string SectionName = "Monitoring:ApplicationPools";

    public int PollIntervalSeconds { get; set; } = 30;
    public int MaxStartAttempts { get; set; } = 3;
    public int StartDelaySeconds { get; set; } = 10;
    public int StartTimeoutSeconds { get; set; } = 30;
    public int StableStateDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Waiting period: a pool is started automatically, and operators are only notified when it
    /// is still down this long after it was first seen down and <see cref="MaxStartAttempts"/>
    /// starts in a row have failed. A successful start is recorded (ApplicationPoolStarted),
    /// never sent. 0 = notify as soon as the starts have failed.
    /// </summary>
    public int SustainedSeconds { get; set; } = 120;
    public List<string> WatchedApplicationPools { get; set; } = new();
}