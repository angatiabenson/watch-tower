using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class AppPoolStopHandler : IActionHandler
{
    private readonly IIisApplicationPoolManager _poolManager;
    private readonly ILogger<AppPoolStopHandler> _logger;

    public ActionType ActionType => ActionType.AppPoolStop;

    public AppPoolStopHandler(
        IIisApplicationPoolManager poolManager,
        ILogger<AppPoolStopHandler> logger)
    {
        _poolManager = poolManager;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!AppPoolStartHandler.TryGetPoolName(parameters, out var name))
            return Task.FromResult(new ActionResult(false, "Missing or invalid AppPoolName parameter"));

        var timeout = AppPoolStartHandler.GetTimeout(parameters);
        _logger.LogInformation("Portal command: stopping app pool '{Name}'", name);

        var ok = _poolManager.StopApplicationPool(name, timeout);
        return Task.FromResult(ok
            ? new ActionResult(true, $"App pool '{name}' stopped successfully")
            : new ActionResult(false, $"Failed to stop app pool '{name}'"));
    }
}
