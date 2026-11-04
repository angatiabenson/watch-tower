using WatchTower.Hosting;

namespace WatchTower.Portal.Actions.Handlers;

/// <summary>Restarts the WatchTower service on request from the portal (ActionType 303).</summary>
public class AgentRestartHandler(IAgentRestarter restarter) : IActionHandler
{
    public ActionType ActionType => ActionType.AgentRestart;

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        // The result is reported before the helper stops the service (it waits ~2 s first).
        var ok = restarter.TryScheduleRestart("requested by the Visibility Portal", out var message);
        return Task.FromResult(new ActionResult(ok, message));
    }
}