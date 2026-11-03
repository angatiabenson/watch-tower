namespace WatchTower.Models;

// Explicit values are part of the on-disk (SQLite int) and on-the-wire (portal) contract.
// Do NOT reorder or renumber existing members — only append with new explicit values.
public enum IncidentType
{
    CpuThresholdBreached = 0,
    CpuSpikeDetected = 1,
    RamThresholdBreached = 2,
    RamSpikeDetected = 3,
    DiskCritical = 4,
    DiskQueueHigh = 5,
    NetworkQueueSaturated = 6,
    ServiceDown = 7,
    ServiceRestarted = 8,
    ServiceRestartFailed = 9,
    ApplicationPoolDown = 10,
    ApplicationPoolStarted = 11,
    ApplicationPoolStartFailed = 12,
}