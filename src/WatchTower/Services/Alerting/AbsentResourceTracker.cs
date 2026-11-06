using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Retires the open alerts of discovered resources (disks, disk queues, network adapters) that
/// have disappeared, and deletes their latest-state snapshot rows. Unlike services or endpoints
/// these are not configured, so nothing else would ever close an alert whose drive was removed
/// (it would stay open and remind forever) or stop reporting the drive's last reading to the portal.
/// </summary>
/// <remarks>
/// <para>Tracked are the resources with an open alert of this kind and, when
/// <c>snapshots</c> is given, the resources with a snapshot row of <c>snapshotKind</c>. Both are
/// retired by the same grace logic below; a resource that is read again later gets a new row.</para>
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
/// <param name="logger">Logger.</param>
/// <param name="snapshots">Store whose snapshot rows of <paramref name="snapshotKind"/> are pruned too; null for alerts only.</param>
/// <param name="snapshotKind">Snapshot table of this resource kind.</param>
public sealed class AbsentResourceTracker(
    string keyPrefix, string what, Func<string, string> reason, bool emptyIsSuspicious, ILogger logger,
    IMetricsStore? snapshots = null, SnapshotKind snapshotKind = default)
{
    public const int MissedPolls = 3;

    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>Missing resources by name (not alert key).</summary>
    private readonly Dictionary<string, (int Polls, DateTime Since)> _missing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the inventory and retires the open alerts and snapshot rows of this kind whose
    /// resource has been missing for long enough. Never throws for a failed read (logged,
    /// nothing retired).
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

        var present = inventory.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Resource name -> open alert key / stored snapshot name, as stored.
        var alerting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Breaches still in their waiting period count too: they would otherwise wait forever.
        foreach (var key in alerts.GetOpenAlerts().Select(a => a.Key).Concat(alerts.GetPendingAlerts().Select(p => p.Key)))
        {
            if (key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                alerting.TryAdd(key[keyPrefix.Length..], key);
        }
        var reported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in snapshots?.GetSnapshotNames(snapshotKind) ?? [])
            reported.TryAdd(name, name);

        // Back, or neither alerting nor reported any more: forget it.
        foreach (var name in _missing.Keys.Where(n => present.Contains(n) || !(alerting.ContainsKey(n) || reported.ContainsKey(n))).ToList())
            _missing.Remove(name);

        var tracked = alerting.Keys.Concat(reported.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tracked.Where(n => !present.Contains(n)).ToList())
        {
            var (polls, since) = _missing.TryGetValue(name, out var seen) ? seen : (0, nowUtc);
            polls++;
            _missing[name] = (polls, since);
            if (polls < MissedPolls || nowUtc - since < GracePeriod)
                continue;

            if (alerting.TryGetValue(name, out var key) &&
                await alerts.RetireAsync(new AlertKey(key), reason(key[keyPrefix.Length..]), ct))
            {
                logger.LogInformation("Retired alert {Key}: missing from {Polls} consecutive listings of {What} since {Since:O}",
                    key, polls, what, since);
            }

            if (snapshots is not null && reported.TryGetValue(name, out var stored) &&
                snapshots.RemoveSnapshots(snapshotKind, [stored]) > 0)
            {
                logger.LogInformation(
                    "Stopped reporting {Name} to the portal: missing from {Polls} consecutive listings of {What} since {Since:O}",
                    stored, polls, what, since);
            }

            _missing.Remove(name);
        }
    }
}