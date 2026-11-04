using WatchTower.Models;
using WatchTower.Services;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

/// <summary>
/// Tells the operator why a resource is shown as stale. A drive, disk, adapter, service, pool or
/// endpoint check that is still listed but can no longer be read keeps its last reading (see
/// <see cref="SnapshotKind"/>), which the metrics batches and the console then mark stale
/// (<see cref="SnapshotFreshnessPolicy"/>). This worker logs one warning when an entry first
/// becomes stale and an information message when it is read again. At most one warning per
/// resource per <see cref="WarningInterval"/>: an entry that goes stale again inside that interval
/// is logged once the interval has passed if it is still stale, and episodes that end before then
/// are counted in the next warning.
/// </summary>
public sealed class StaleSnapshotWorker(
    IMetricsStore metricsStore,
    SnapshotFreshnessPolicy freshness,
    TimeProvider timeProvider,
    ILogger<StaleSnapshotWorker> logger) : BackgroundService
{
    /// <summary>How often the snapshot tables are checked. The windows are at least 10 minutes.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>At most one stale warning per resource in this interval.</summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromHours(1);

    private readonly record struct Key(SnapshotKind Kind, string Name);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key x, Key y) => x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode(Key key) => HashCode.Combine(key.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }

    private sealed class Warned
    {
        public DateTime At;
        public int Suppressed;
    }

    /// <summary>Entries currently stale, and whether this episode was logged.</summary>
    private readonly Dictionary<Key, bool> _stale = new(KeyComparer.Instance);

    /// <summary>When each resource was last warned about, and the episodes suppressed since.</summary>
    private readonly Dictionary<Key, Warned> _warned = new(KeyComparer.Instance);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Waiting first gives the monitors their first poll after a restart: rows read
                // before it are old, but are about to be read again.
                await Task.Delay(Tick, timeProvider, stoppingToken);
                CheckOnce();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not check the snapshot readings for staleness");
            }
        }
    }

    /// <summary>One check of every snapshot entry. Exposed for deterministic testing.</summary>
    internal void CheckOnce()
    {
        var now = UtcNow;
        var present = new HashSet<Key>(KeyComparer.Instance);

        void Observe(SnapshotKind kind, string name, DateTime readingAt, string? status = null)
        {
            var key = new Key(kind, name);
            present.Add(key);
            var window = freshness.Window(kind, name);
            if (!SnapshotFreshness.IsStale(readingAt, now, window))
            {
                if (!_stale.Remove(key, out var episodeWarned))
                    return;
                if (episodeWarned)
                {
                    logger.LogInformation("{What} {Name} is being read again (reading at {ReadingAt:O}); it is no longer reported as stale",
                        What(kind), name, readingAt);
                }
                else if (_warned.TryGetValue(key, out var earlier))
                {
                    earlier.Suppressed++; // an episode that ended inside the rate limit, never logged
                }
                return;
            }

            if (_stale.TryGetValue(key, out var warnedAlready) && warnedAlready)
                return; // one warning per episode

            if (_warned.TryGetValue(key, out var warned) && now - warned.At < WarningInterval)
            {
                _stale[key] = false; // logged once the rate limit allows, if it is still stale then
                return;
            }

            var suppressed = warned?.Suppressed ?? 0;
            _warned[key] = new Warned { At = now };
            _stale[key] = true;
            logger.LogWarning(
                "{What} {Name} has had no new reading since {ReadingAt:O} (freshness window {WindowMinutes:0.#} min): it is still " +
                "reported with that reading, marked stale. {Reason}{Suppressed}",
                What(kind), name, readingAt, window.TotalMinutes, Reason(kind, status),
                suppressed > 0 ? $" ({suppressed} shorter stale episode(s) since the last warning were not logged)" : string.Empty);
        }

        foreach (var m in metricsStore.GetLatestDiskMetrics())
            Observe(SnapshotKind.Disk, m.DriveName, m.Timestamp);
        foreach (var m in metricsStore.GetLatestDiskQueueMetrics())
            Observe(SnapshotKind.DiskQueue, m.InstanceName, m.Timestamp);
        foreach (var m in metricsStore.GetLatestNetworkMetrics())
            Observe(SnapshotKind.Network, m.InterfaceName, m.Timestamp);
        foreach (var m in metricsStore.GetLatestServiceStatuses())
            Observe(SnapshotKind.Service, m.ServiceName, m.Timestamp);
        foreach (var m in metricsStore.GetLatestApplicationPoolStatuses())
            Observe(SnapshotKind.ApplicationPool, m.ApplicationPoolName, m.Timestamp);
        foreach (var e in metricsStore.GetLatestEndpointStatuses())
            Observe(SnapshotKind.Endpoint, e.Name, e.Timestamp, e.Status);

        // Removed resources: forget them (a resource that comes back starts afresh), but keep the
        // rate limit of a recent warning so a resource that keeps disappearing is not logged each time.
        foreach (var key in _stale.Keys.Where(k => !present.Contains(k)).ToList())
            _stale.Remove(key);
        foreach (var key in _warned.Where(w => !present.Contains(w.Key) && now - w.Value.At >= WarningInterval).Select(w => w.Key).ToList())
            _warned.Remove(key);
    }

    internal static string What(SnapshotKind kind) => kind switch
    {
        SnapshotKind.Disk => "Drive",
        SnapshotKind.DiskQueue => "Physical disk",
        SnapshotKind.Network => "Network interface",
        SnapshotKind.Service => "Service",
        SnapshotKind.ApplicationPool => "Application pool",
        SnapshotKind.Endpoint => "Endpoint check",
        _ => kind.ToString(),
    };

    internal static string Reason(SnapshotKind kind, string? status) => kind switch
    {
        SnapshotKind.Disk =>
            "The drive is still listed but could not be read (for example a fixed drive that is not ready), or the disk monitor is failing.",
        SnapshotKind.DiskQueue =>
            "The PhysicalDisk performance counter no longer returns this disk, or the disk monitor is failing.",
        SnapshotKind.Network =>
            "The Network Interface performance counter no longer returns this adapter, or the network monitor is failing.",
        SnapshotKind.Service =>
            "The service monitor has not checked it (a restart still in progress, or the monitor is failing).",
        SnapshotKind.ApplicationPool =>
            "The application pool monitor has not checked it (a start still in progress, IIS not answering, or the monitor is failing).",
        SnapshotKind.Endpoint when status == EndpointStatus.ConfigurationError =>
            "The check is configured but invalid, so it does not run: fix its configuration.",
        SnapshotKind.Endpoint =>
            "The check has not completed a run, or the endpoint monitor is failing.",
        _ => string.Empty,
    };
}