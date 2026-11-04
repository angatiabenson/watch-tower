namespace WatchTower.Services.Alerting;

/// <summary>
/// Per-key "how long has the condition been clear" tracker for metric alerts whose readings
/// flap around their threshold (CPU, RAM, disks, queues). A worker reports every reading; the
/// alert is resolved only after the condition stayed clear for the whole recovery window, so a
/// single good sample between bad ones does not produce a resolved/opened notification pair.
/// Not thread-safe: each worker owns its instance and polls sequentially.
/// </summary>
public sealed class RecoveryDebouncer
{
    private readonly Dictionary<string, DateTime> _clearSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The condition is (again) present: restart the clear window.</summary>
    public void Breached(AlertKey key) => _clearSince.Remove(key.Value);

    /// <summary>The condition is clear now; true once it has been clear for <paramref name="window"/>.</summary>
    public bool Clear(AlertKey key, DateTime nowUtc, TimeSpan window)
    {
        if (!_clearSince.TryGetValue(key.Value, out var since))
        {
            since = nowUtc;
            _clearSince[key.Value] = since;
        }
        return nowUtc - since >= window;
    }
}