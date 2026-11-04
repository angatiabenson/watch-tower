using WatchTower.Updates;

namespace WatchTower.Portal.Actions.Handlers;

/// <summary>
/// Portal-initiated update (ActionType 304): check the feed, download and verify the release,
/// then start the normal restart countdown so the install is still visible (and cancellable)
/// in the local console. Optional parameter <c>Version</c> must match the offered release.
/// </summary>
public class AgentUpdateHandler(UpdateService updates) : IActionHandler
{
    public ActionType ActionType => ActionType.AgentUpdate;

    public async Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var status = await updates.CheckAsync(ct);
        if (status.Available is null)
            return new ActionResult(status.Error is null,
                status.Error ?? $"Already up to date ({status.CurrentVersion}).");

        if (parameters.TryGetValue("Version", out var pinned) && !string.IsNullOrWhiteSpace(pinned) &&
            !string.Equals(pinned.Trim().TrimStart('v'), status.Available.Version, StringComparison.OrdinalIgnoreCase))
            return new ActionResult(false,
                $"Requested version {pinned} is not the version offered by the feed ({status.Available.Version}).");

        if (status.Phase == UpdatePhase.ReadyToInstall)
        {
            updates.StartCountdown();
            return new ActionResult(true, $"Installing {status.Available.Version} after the restart countdown.");
        }

        var started = updates.TryStartDownload(countdown: true, out var message);
        return new ActionResult(started, started
            ? $"Downloading {status.Available.Version}; it installs automatically after the restart countdown."
            : message);
    }
}