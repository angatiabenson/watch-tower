namespace WatchTower.Models;

public enum IncidentType
{
    CpuThresholdBreached,
    RamThresholdBreached,
    DiskCritical,
    ServiceDown,
    ServiceRestartFailed
}
