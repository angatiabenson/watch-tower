namespace WatchTower.Portal.Actions;

public enum ActionType
{
    // Windows Services: 101–103
    ServiceStart   = 101,
    ServiceStop    = 102,
    ServiceRestart = 103,

    // IIS Application Pools: 201–204
    AppPoolStart   = 201,
    AppPoolStop    = 202,
    AppPoolRestart = 203,
    AppPoolRecycle = 204,

    // Agent: 301–303
    ForceMetricSnapshot = 301,
    ForceConfigSync     = 302,
    AgentRestart        = 303,

    // Configuration: 401
    ApplyConfiguration = 401,
}
