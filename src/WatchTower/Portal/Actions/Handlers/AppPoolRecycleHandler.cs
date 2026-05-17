using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class AppPoolRecycleHandler : IActionHandler
{
    private readonly IIisApplicationPoolManager _poolManager;
    private readonly ILogger<AppPoolRecycleHandler> _logger;

    public ActionType ActionType => ActionType.AppPoolRecycle;

    public AppPoolRecycleHandler(
        IIisApplicationPoolManager poolManager,
        ILogger<AppPoolRecycleHandler> logger)
    {
        _poolManager = poolManager;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!AppPoolStartHandler.TryGetPoolName(parameters, out var name))
            return Task.FromResult(new ActionResult(false, "Missing or invalid AppPoolName parameter"));

        _logger.LogInformation("Portal command: recycling app pool '{Name}'", name);

        var ok = _poolManager.RecycleApplicationPool(name, TimeSpan.FromSeconds(30));
        return Task.FromResult(ok
            ? new ActionResult(true, $"App pool '{name}' recycled successfully")
            : new ActionResult(false, $"Failed to recycle app pool '{name}'"));
    }
}
