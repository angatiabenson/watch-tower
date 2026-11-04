using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

/// <summary>
/// Applies <see cref="SnapshotFreshness"/> with the current monitor configuration (local or
/// pushed by remote config): the poll interval of the monitor that reads each kind of resource,
/// and the interval and timeout of each endpoint check.
/// </summary>
public sealed class SnapshotFreshnessPolicy(
    IOptionsMonitor<DiskMonitorOptions> disk,
    IOptionsMonitor<NetworkMonitorOptions> network,
    IOptionsMonitor<ServiceMonitorOptions> services,
    IOptionsMonitor<ApplicationPoolMonitorOptions> appPools,
    IOptionsMonitor<EndpointMonitorOptions> endpoints)
{
    /// <summary>The windows of the default configuration (for callers built without options).</summary>
    public static SnapshotFreshnessPolicy Default { get; } = new(
        new FixedOptions<DiskMonitorOptions>(new()),
        new FixedOptions<NetworkMonitorOptions>(new()),
        new FixedOptions<ServiceMonitorOptions>(new()),
        new FixedOptions<ApplicationPoolMonitorOptions>(new()),
        new FixedOptions<EndpointMonitorOptions>(new()));

    /// <summary>
    /// The freshness window of the snapshot entry <paramref name="name"/> of <paramref name="kind"/>.
    /// Drives and physical disks are read by the disk monitor, so both use its poll interval. An
    /// endpoint name that is not (or no longer) configured gets the window of a default check.
    /// </summary>
    public TimeSpan Window(SnapshotKind kind, string name) => kind switch
    {
        SnapshotKind.Disk or SnapshotKind.DiskQueue => SnapshotFreshness.PolledWindow(disk.CurrentValue.PollIntervalSeconds),
        SnapshotKind.Network => SnapshotFreshness.PolledWindow(network.CurrentValue.PollIntervalSeconds),
        SnapshotKind.Service => SnapshotFreshness.PolledWindow(services.CurrentValue.PollIntervalSeconds),
        SnapshotKind.ApplicationPool => SnapshotFreshness.PolledWindow(appPools.CurrentValue.PollIntervalSeconds),
        SnapshotKind.Endpoint => EndpointWindow(name),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>True when the reading taken at <paramref name="readingAt"/> is older than its window at <paramref name="now"/>.</summary>
    public bool IsStale(SnapshotKind kind, string name, DateTime readingAt, DateTime now) =>
        SnapshotFreshness.IsStale(readingAt, now, Window(kind, name));

    private static readonly EndpointCheckOptions DefaultCheck = new();

    /// <summary>Name index of the endpoint checks for one options instance (immutable once built).</summary>
    private sealed class EndpointIndex(EndpointMonitorOptions options, int count, Dictionary<string, EndpointCheckOptions> byName)
    {
        public EndpointMonitorOptions Options { get; } = options;
        public int Count { get; } = count;
        public Dictionary<string, EndpointCheckOptions> ByName { get; } = byName;
    }

    /// <summary>Rebuilt when the options instance (a configuration reload) or its list length changes.</summary>
    private volatile EndpointIndex? _endpointIndex;

    private TimeSpan EndpointWindow(string name)
    {
        var options = endpoints.CurrentValue;
        var index = _endpointIndex;
        if (index is null || !ReferenceEquals(index.Options, options) || index.Count != options.Endpoints.Count)
        {
            // The first entry of a name is the one the endpoint monitor runs (names match
            // case-insensitively, trimmed, like its schedule).
            var byName = new Dictionary<string, EndpointCheckOptions>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in options.Endpoints)
            {
                var key = e?.Name?.Trim();
                if (!string.IsNullOrEmpty(key))
                    byName.TryAdd(key, e!);
            }

            index = new EndpointIndex(options, options.Endpoints.Count, byName);
            _endpointIndex = index; // a racing rebuild only repeats this work
        }

        var check = index.ByName.GetValueOrDefault(name?.Trim() ?? string.Empty, DefaultCheck);
        return SnapshotFreshness.EndpointWindow(check.IntervalSeconds, check.TimeoutSeconds);
    }

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}