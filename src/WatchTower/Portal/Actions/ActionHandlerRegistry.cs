namespace WatchTower.Portal.Actions;

public class ActionHandlerRegistry
{
    private readonly IReadOnlyDictionary<ActionType, IActionHandler> _handlers;
    private readonly ILogger<ActionHandlerRegistry> _logger;

    public ActionHandlerRegistry(
        IEnumerable<IActionHandler> handlers,
        ILogger<ActionHandlerRegistry> logger)
    {
        _handlers = handlers.ToDictionary(h => h.ActionType);
        _logger = logger;
    }

    public async Task<ActionResult> ExecuteAsync(
        int actionTypeId,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(typeof(ActionType), actionTypeId))
        {
            _logger.LogWarning("Unknown ActionType ID: {Id}", actionTypeId);
            return new ActionResult(false, $"Unknown action type: {actionTypeId}");
        }

        var actionType = (ActionType)actionTypeId;

        if (!_handlers.TryGetValue(actionType, out var handler))
        {
            _logger.LogWarning("No handler registered for ActionType {Type}", actionType);
            return new ActionResult(false, $"No handler for action type: {actionType}");
        }

        try
        {
            return await handler.ExecuteAsync(parameters, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action handler {Type} threw an exception", actionType);
            return new ActionResult(false, $"Handler error: {ex.Message}");
        }
    }
}