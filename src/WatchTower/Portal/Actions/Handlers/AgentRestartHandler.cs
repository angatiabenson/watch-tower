namespace WatchTower.Portal.Actions.Handlers;

public class AgentRestartHandler : IActionHandler
{
    public ActionType ActionType => ActionType.AgentRestart;

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        return Task.FromResult(new ActionResult(false, "AgentRestart is reserved and not yet implemented"));
    }
}
