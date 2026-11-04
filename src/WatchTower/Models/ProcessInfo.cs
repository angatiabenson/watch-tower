namespace WatchTower.Models;

public record ProcessInfo(
    string Name,
    int Pid,
    double CpuSeconds,
    double MemoryMb,
    double CpuPercent = 0
);