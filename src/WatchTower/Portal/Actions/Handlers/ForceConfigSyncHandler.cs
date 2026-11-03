namespace WatchTower.Portal.Actions.Handlers;

public class ForceConfigSyncHandler : IActionHandler
{
    private readonly IConfigSyncTrigger _trigger;
    private readonly ILogger<ForceConfigSyncHandler> _logger;

    public ActionType ActionType => ActionType.ForceConfigSync;

    public ForceConfigSyncHandler(
        IConfigSyncTrigger trigger,
        ILogger<ForceConfigSyncHandler> logger)
    {
        _trigger = trigger;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        _logger.LogInformation("Portal command: force config sync");
        _trigger.Signal();
        return Task.FromResult(new ActionResult(true, "Config sync triggered"));
    }
}