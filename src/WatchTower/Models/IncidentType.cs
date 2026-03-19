namespace WatchTower.Models;

public enum IncidentType
{
    CpuThresholdBreached,
    RamThresholdBreached,
    DiskCritical,
    DiskQueueHigh,
    NetworkQueueSaturated,
    ServiceDown,
    ServiceRestartFailed
}
