namespace WatchTower.Services;

/// <summary>
/// What <see cref="Abstractions.IMetricsStore.RemoveSnapshotsExcept"/> does to the stored
/// latest-state rows of configured resources. Names match case-insensitively (OrdinalIgnoreCase),
/// like the workers' schedules and the alert keys: a row whose name only differs in case from a
/// configured name is kept and renamed to the configured spelling, so a case-only rename in the
/// configuration never loses the resource's last reading.
/// </summary>
/// <param name="Remove">Stored names to delete: not configured, or a second spelling of one that is.</param>
/// <param name="Rename">Stored names to rename to their configured spelling.</param>
internal sealed record SnapshotPrunePlan(
    IReadOnlyList<string> Remove,
    IReadOnlyList<(string Stored, string Configured)> Rename)
{
    public static SnapshotPrunePlan Create(IEnumerable<string> stored, IEnumerable<string> keep)
    {
        // The first spelling of a name wins, as the workers poll the first of duplicates.
        var configured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in keep)
        {
            if (!string.IsNullOrWhiteSpace(name))
                configured.TryAdd(name, name);
        }

        var storedNames = stored.ToList();
        // A row already stored under the configured spelling is the one kept.
        var claimed = new HashSet<string>(
            storedNames.Where(s => configured.TryGetValue(s, out var c) && string.Equals(s, c, StringComparison.Ordinal)),
            StringComparer.OrdinalIgnoreCase);

        var remove = new List<string>();
        var rename = new List<(string, string)>();
        foreach (var name in storedNames)
        {
            if (!configured.TryGetValue(name, out var spelling))
                remove.Add(name);
            else if (string.Equals(name, spelling, StringComparison.Ordinal))
                continue;
            else if (!claimed.Add(spelling))
                remove.Add(name);
            else
                rename.Add((name, spelling));
        }

        return new SnapshotPrunePlan(remove, rename);
    }
}