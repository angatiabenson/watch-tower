namespace WatchTower.Models;

/// <summary>
/// The resources of one kind that exist right now: fixed drives, <c>PhysicalDisk</c> or
/// <c>Network Interface</c> counter instances. Includes resources whose metrics could not be read
/// this time (a drive that is not ready, a counter that failed), because the metric reads skip
/// those and they must not look removed. Used to retire the alerts of resources that are gone.
/// </summary>
/// <param name="Names">Names as the matching metrics report them (e.g. <c>C:\</c>, <c>0 C:</c>).</param>
public sealed record ResourceInventory(IReadOnlyList<string> Names);