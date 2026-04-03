namespace WatchTower.Models;

public enum IncidentType
{
    CpuThresholdBreached,
    CpuSpikeDetected,
    RamThresholdBreached,
    RamSpikeDetected,
    DiskCritical,
    DiskQueueHigh,
    NetworkQueueSaturated,
    ServiceDown,
    ServiceRestarted,
    ServiceRestartFailed,
    ApplicationPoolDown,
    ApplicationPoolStarted,
    ApplicationPoolStartFailed,
}
