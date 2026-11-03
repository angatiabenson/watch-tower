using System.Collections.Concurrent;

namespace WatchTower.Hosting;

/// <summary>Liveness of the monitoring workers, used to gate the dead man's switch.</summary>
public interface IWorkerHealth
{
    /// <summary>
    /// Records that <paramref name="worker"/> completed a cycle (or made progress) now.
    /// <paramref name="interval"/> is its current poll interval, which sets its staleness limit.
    /// </summary>
    void ReportSuccess(string worker, TimeSpan interval);

    /// <summary>Every worker that has reported so far.</summary>
    IReadOnlyList<WorkerHealthEntry> Snapshot();

    /// <summary>Workers with no successful cycle for longer than their staleness limit.</summary>
    IReadOnlyList<WorkerHealthEntry> GetStale();
}

/// <param name="StaleAfter">
/// 3× the poll interval, but at least <see cref="WorkerHealthRegistry.MinimumStaleAfter"/> so a
/// cycle that is legitimately slow (an SMTP timeout, an IIS start) is not reported as dead.
/// </param>
public sealed record WorkerHealthEntry(string Worker, DateTimeOffset LastSuccessAt, TimeSpan Interval, TimeSpan StaleAfter, bool IsStale);

public sealed class WorkerHealthRegistry(TimeProvider time) : IWorkerHealth
{
    public static readonly TimeSpan MinimumStaleAfter = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, TimeSpan Interval)> _workers =
        new(StringComparer.OrdinalIgnoreCase);

    public void ReportSuccess(string worker, TimeSpan interval) =>
        _workers[worker] = (time.GetUtcNow(), interval);

    public IReadOnlyList<WorkerHealthEntry> Snapshot()
    {
        var now = time.GetUtcNow();
        return _workers
            .Select(kv =>
            {
                var staleAfter = StaleAfter(kv.Value.Interval);
                return new WorkerHealthEntry(kv.Key, kv.Value.At, kv.Value.Interval, staleAfter, now - kv.Value.At > staleAfter);
            })
            .OrderBy(e => e.Worker, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<WorkerHealthEntry> GetStale() => Snapshot().Where(e => e.IsStale).ToList();

    public static TimeSpan StaleAfter(TimeSpan interval)
    {
        var threefold = interval * 3;
        return threefold > MinimumStaleAfter ? threefold : MinimumStaleAfter;
    }
}

/// <summary>Worker names used for liveness reporting (also shown in the console status).</summary>
public static class WorkerNames
{
    public const string Cpu = "cpu";
    public const string Ram = "ram";
    public const string Disk = "disk";
    public const string Network = "network";
    public const string Services = "services";
    public const string AppPools = "apppools";
    public const string Endpoints = "endpoints";
}