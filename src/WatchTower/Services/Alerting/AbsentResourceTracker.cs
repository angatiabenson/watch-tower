using WatchTower.Models;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Retires the open alerts of discovered resources (disks, disk queues, network adapters) that
/// have disappeared. Unlike services or endpoints these are not configured, so nothing else
/// would ever close an alert whose drive was removed; it would stay open (and remind) forever.
/// </summary>
/// <remarks>
/// <para>A resource is only treated as gone after a grace period, so a removable or USB disk
/// that is re-plugged or a network adapter that blips keeps its alert: it must be missing from
/// <see cref="MissedPolls"/> consecutive successful inventory reads <b>and</b> for at least
/// <see cref="GracePeriod"/> since the first of them. Seeing it again resets both.</para>
/// <para>Only successful reads count. A read that throws tells nothing: it neither counts as a
/// miss nor resets one. The same goes for an empty read when <c>emptyIsSuspicious</c> (a host
/// always has a system drive, so zero drives means the read is broken, not that every drive was
/// removed). Zero network adapters can be genuine and counts normally.</para>
/// <para>Tracking is in memory: after a restart an alert persisted for a resource that is gone
/// is retired once the grace period has passed since startup. Not thread-safe: each worker owns
/// its instances and polls sequentially.</para>
/// </remarks>
/// <param name="keyPrefix">Alert key prefix of the resource kind, e.g. <see cref="AlertKey.DiskPrefix"/>.</param>
/// <param name="what">Kind of resource for logs, e.g. "drives".</param>
/// <param name="reason">Retirement reason for a resource name, e.g. "Disk D: is no longer present".</param>
/// <param name="emptyIsSuspicious">Whether an empty inventory is taken as a broken read.</param>
public sealed class AbsentResourceTracker(
    string keyPrefix, string what, Func<string, string> reason, bool emptyIsSuspicious, ILogger logger)
{
    public const int MissedPolls = 3;

    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (int Polls, DateTime Since)> _missing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the inventory and retires the open alerts of this kind whose resource has been
    /// missing for long enough. Never throws for a failed read (logged, nothing retired).
    /// </summary>
    public async Task ReconcileAsync(IAlertManager alerts, Func<ResourceInventory?> readInventory, DateTime nowUtc, CancellationToken ct)
    {
        ResourceInventory? inventory;
        try
        {
            inventory = readInventory();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Listing {What} failed; not checking for removed {What} this cycle", what, what);
            return;
        }
        if (inventory is null)
            return; // no inventory available: nothing is known
        if (inventory.Names.Count == 0 && emptyIsSuspicious)
        {
            logger.LogWarning("Listing {What} returned none; not treating them as removed", what);
            return;
        }

        var present = inventory.Names.Select(n => keyPrefix + n).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var open = alerts.GetOpenAlerts()
            .Select(a => a.Key)
            .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Back, or no longer alerting: forget it.
        foreach (var key in _missing.Keys.Where(k => present.Contains(k) || !open.Contains(k)).ToList())
            _missing.Remove(key);

        foreach (var key in open.Where(k => !present.Contains(k)))
        {
            var (polls, since) = _missing.TryGetValue(key, out var seen) ? seen : (0, nowUtc);
            polls++;
            _missing[key] = (polls, since);
            if (polls < MissedPolls || nowUtc - since < GracePeriod)
                continue;

            var name = key[keyPrefix.Length..];
            if (await alerts.RetireAsync(new AlertKey(key), reason(name), ct))
                logger.LogInformation("Retired alert {Key}: missing from {Polls} consecutive listings of {What} since {Since:O}",
                    key, polls, what, since);
            _missing.Remove(key);
        }
    }
}