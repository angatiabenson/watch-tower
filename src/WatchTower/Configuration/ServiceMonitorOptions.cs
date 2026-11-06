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

    /// <summary>After the agent starts, down services are only recorded (not restarted) for this
    /// long, so delayed-auto-start services coming up after a reboot are not restarted mid-start.</summary>
    public int StartupGraceSeconds { get; set; } = 120;

    /// <summary>
    /// Waiting period: a service is restarted automatically, and operators are only notified
    /// when it is still down this long after it was first seen down and
    /// <see cref="MaxRestartAttempts"/> restarts in a row have failed. A successful restart is
    /// recorded (ServiceRestarted), never sent. 0 = notify as soon as the restarts have failed.
    /// </summary>
    public int SustainedSeconds { get; set; } = 120;

    public List<string> WatchedServices { get; set; } = new();
}