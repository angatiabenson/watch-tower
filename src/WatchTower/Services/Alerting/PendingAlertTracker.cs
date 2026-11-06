namespace WatchTower.Services.Alerting;

/// <summary>
/// The waiting periods of every alert (see <see cref="IAlertManager.ObserveBreach"/>): which
/// conditions are present but not alerted yet (<see cref="PendingAlert"/>), and which cleared
/// within their waiting period (summarised in the daily report). Kept in memory only: after a
/// restart a breach that was waiting simply starts over. Thread-safe.
/// </summary>
/// <remarks>
/// <para>A breach is tracked per alert key and severity. Each severity waits its own period
/// from when it was first seen, so a disk going from warning to critical waits the critical
/// period from the moment it became critical. A reading at a lower severity ends the waits of
/// the higher ones (the condition is no longer that bad) but keeps the lower ones running.</para>
/// <para>What counts as continuous follows the CPU and memory monitors: one reading back under
/// the threshold ends the wait (<see cref="Clear"/>). A reading that could not be taken
/// changes nothing.</para>
/// </remarks>
public sealed class PendingAlertTracker
{
    /// <summary>Most cleared breaches remembered for the daily report (the oldest are dropped first).</summary>
    internal const int MaxShortBreaches = 10_000;

    /// <summary>How long cleared breaches are remembered for the daily report.</summary>
    internal static readonly TimeSpan ShortBreachRetention = TimeSpan.FromHours(48);

    /// <summary>Longest waiting period (one day), as for every <c>SustainedSeconds</c> setting.</summary>
    public const int MaxSeconds = 86_400;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SortedList<AlertSeverity, PendingAlert>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Kind, DateTime At, TimeSpan Duration)> _shortBreaches = new();

    /// <summary>A <c>SustainedSeconds</c> setting as a waiting period: negative is 0, at most one day.</summary>
    public static TimeSpan Period(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxSeconds));

    /// <summary>
    /// The condition behind <paramref name="key"/> is present at <paramref name="severity"/>.
    /// Returns true when it should be raised now: its alert is already open at this severity or
    /// higher, or it has been present at this severity for <paramref name="alertAfter"/>.
    /// </summary>
    /// <param name="openSeverity">Severity of the key's open alert; null when none is open.</param>
    public bool Observe(AlertKey key, AlertSeverity severity, TimeSpan alertAfter, string title, DateTime nowUtc,
        AlertSeverity? openSeverity)
    {
        if (alertAfter < TimeSpan.Zero)
            alertAfter = TimeSpan.Zero;

        lock (_lock)
        {
            var levels = _pending.GetValueOrDefault(key.Value);

            // Not that bad any more: the waits of higher severities are over.
            if (levels is not null)
            {
                var higher = levels.Where(l => l.Key > severity).Select(l => l.Value).ToList();
                foreach (var gone in higher)
                    levels.Remove(gone.Severity);
                if (higher.Count > 0)
                    RecordShortLocked(key.Value, higher.Min(p => p.Since), nowUtc);
            }

            if (openSeverity is { } open && open >= severity)
            {
                // Already alerted at least this badly: nothing to wait for.
                RemoveUpToLocked(key.Value, severity);
                return true;
            }

            if (levels is null)
            {
                levels = new SortedList<AlertSeverity, PendingAlert>();
                _pending[key.Value] = levels;
            }

            var entry = levels.TryGetValue(severity, out var existing)
                ? existing with { Title = title, AlertAfter = alertAfter, LastSeenAt = nowUtc }
                : new PendingAlert(key.Value, severity, title, nowUtc, alertAfter, nowUtc);
            levels[severity] = entry;
            return nowUtc - entry.Since >= alertAfter;
        }
    }

    /// <summary>
    /// The condition behind <paramref name="key"/> is not present: a breach still waiting is
    /// dropped without notifying anyone, and counted for the daily report.
    /// </summary>
    public void Clear(AlertKey key, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_pending.Remove(key.Value, out var levels) || levels.Count == 0)
                return;
            RecordShortLocked(key.Value, levels.Values.Min(p => p.Since), nowUtc);
        }
    }

    /// <summary>The alert was raised at <paramref name="severity"/>: waits up to that severity are over.</summary>
    public void Raised(AlertKey key, AlertSeverity severity)
    {
        lock (_lock)
            RemoveUpToLocked(key.Value, severity);
    }

    /// <summary>What the key watched is no longer monitored: forget its waits (not counted).</summary>
    public void Forget(AlertKey key)
    {
        lock (_lock)
            _pending.Remove(key.Value);
    }

    /// <summary>Keys with a breach still waiting.</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_lock)
            return _pending.Keys.ToList();
    }

    /// <summary>Breaches still waiting, oldest first.</summary>
    public IReadOnlyList<PendingAlert> Snapshot()
    {
        lock (_lock)
        {
            return _pending.Values.SelectMany(l => l.Values)
                .OrderBy(p => p.Since).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ThenByDescending(p => p.Severity)
                .ToList();
        }
    }

    /// <summary>Breaches that cleared within their waiting period since <paramref name="sinceUtc"/>, per kind, most frequent first.</summary>
    public IReadOnlyList<ShortBreachSummary> ShortBreaches(DateTime sinceUtc)
    {
        lock (_lock)
        {
            return _shortBreaches
                .Where(b => b.At >= sinceUtc)
                .GroupBy(b => b.Kind)
                .Select(g => new ShortBreachSummary(g.Key, AlertKinds.Label(g.Key), g.Count(), g.Max(b => b.Duration)))
                .OrderByDescending(s => s.Count).ThenBy(s => s.Label, StringComparer.Ordinal)
                .ToList();
        }
    }

    private void RemoveUpToLocked(string key, AlertSeverity severity)
    {
        if (!_pending.TryGetValue(key, out var levels))
            return;
        foreach (var level in levels.Keys.Where(l => l <= severity).ToList())
            levels.Remove(level);
        if (levels.Count == 0)
            _pending.Remove(key);
    }

    private void RecordShortLocked(string key, DateTime since, DateTime nowUtc)
    {
        _shortBreaches.Enqueue((AlertKinds.Of(key), nowUtc, nowUtc - since));
        while (_shortBreaches.Count > MaxShortBreaches ||
               (_shortBreaches.Count > 0 && nowUtc - _shortBreaches.Peek().At > ShortBreachRetention))
            _shortBreaches.Dequeue();
    }
}