namespace WatchTower.Portal.Actions;

public enum ActionType
{
    // Windows Services: 101–103
    ServiceStart = 101,
    ServiceStop = 102,
    ServiceRestart = 103,

    // IIS Application Pools: 201–204
    AppPoolStart = 201,
    AppPoolStop = 202,
    AppPoolRestart = 203,
    AppPoolRecycle = 204,

    // Agent: 301–303
    ForceMetricSnapshot = 301,
    ForceConfigSync = 302,
    AgentRestart = 303,

    /// <summary>
    /// Check for, download and install the newest release (parameter <c>Version</c> optionally
    /// pins the version that must be offered). The agent restarts as part of the install.
    /// </summary>
    AgentUpdate = 304,

    // Configuration: 401
    ApplyConfiguration = 401,

    // Notifications: 501

    /// <summary>
    /// Send a test message through the notification channels (optional parameter <c>Channel</c>
    /// limits it to one channel by name). Severity filters and maintenance do not apply.
    /// </summary>
    SendTestNotification = 501,
}