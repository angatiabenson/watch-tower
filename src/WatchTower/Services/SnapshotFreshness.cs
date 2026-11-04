namespace WatchTower.Services;

/// <summary>
/// How long a snapshot reading (drive, physical disk, network interface, service, application
/// pool, endpoint check) stays fresh. Snapshot rows are current state: a resource that is still
/// listed but can no longer be read keeps its last reading and that reading's timestamp until it
/// is removed, so "stale" is computed from the timestamp and the resource's freshness window.
/// The one place the windows are defined: the metrics batches (<c>isStale</c>), the console
/// dashboard and the stale-reading warning all use it.
/// </summary>
/// <remarks>
/// Kept free of other WatchTower types so the AgentSimulator sample can link it unchanged.
/// </remarks>
public static class SnapshotFreshness
{
    /// <summary>No window is shorter than this, however short the interval.</summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(10);

    /// <summary>Polled resources: a reading is stale after this many missed polls.</summary>
    public const int PolledIntervals = 3;

    /// <summary>Endpoint checks: a result is stale after this many intervals plus the check's timeout.</summary>
    public const int EndpointIntervals = 2;

    /// <summary>Endpoint interval and timeout ranges a valid check has (see <c>EndpointCheck</c>).</summary>
    public const int EndpointMinIntervalSeconds = 5;

    public const int EndpointMaxIntervalSeconds = 86_400;

    public const int EndpointMaxTimeoutSeconds = 120;

    /// <summary>
    /// Drives, physical disks, network interfaces, services and application pools:
    /// max(3 × the monitor's poll interval, 10 min). The interval is taken as the monitor uses it
    /// (at least 1 s).
    /// </summary>
    public static TimeSpan PolledWindow(int pollIntervalSeconds) =>
        AtLeastMinimum(PolledIntervals * (double)Math.Max(1, pollIntervalSeconds));

    /// <summary>
    /// Endpoint checks: max(2 × the check's interval + its timeout, 10 min). Interval and timeout
    /// are clamped to the ranges of a valid check (5 to 86400 s and 1 to 120 s), so a check whose
    /// configuration is invalid (status ConfigurationError) still gets a bounded window.
    /// </summary>
    public static TimeSpan EndpointWindow(int intervalSeconds, int timeoutSeconds) =>
        AtLeastMinimum(
            EndpointIntervals * (double)Math.Clamp(intervalSeconds, EndpointMinIntervalSeconds, EndpointMaxIntervalSeconds) +
            Math.Clamp(timeoutSeconds, 1, EndpointMaxTimeoutSeconds));

    /// <summary>
    /// True when the reading taken at <paramref name="readingAt"/> is older than
    /// <paramref name="window"/> at <paramref name="now"/>. A reading exactly at the edge of its
    /// window is still fresh, and one from the future (clock change) is fresh.
    /// </summary>
    public static bool IsStale(DateTime readingAt, DateTime now, TimeSpan window) =>
        ToUtc(now) - ToUtc(readingAt) > window;

    private static TimeSpan AtLeastMinimum(double seconds)
    {
        var window = TimeSpan.FromSeconds(seconds);
        return window > MinimumWindow ? window : MinimumWindow;
    }

    /// <summary>Stored timestamps are UTC; an unspecified kind is taken as UTC.</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}