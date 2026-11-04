using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class AppPoolRestartHandler : IActionHandler
{
    private readonly IIisApplicationPoolManager _poolManager;
    private readonly ILogger<AppPoolRestartHandler> _logger;

    public ActionType ActionType => ActionType.AppPoolRestart;

    public AppPoolRestartHandler(
        IIisApplicationPoolManager poolManager,
        ILogger<AppPoolRestartHandler> logger)
    {
        _poolManager = poolManager;
        _logger = logger;
    }

    public async Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!AppPoolStartHandler.TryGetPoolName(parameters, out var name))
            return new ActionResult(false, "Missing or invalid AppPoolName parameter");

        var timeout = AppPoolStartHandler.GetTimeout(parameters);
        _logger.LogInformation("Portal command: restarting app pool '{Name}'", name);

        await _poolManager.StopApplicationPoolAsync(name, timeout, ct);
        var ok = await _poolManager.StartApplicationPoolAsync(name, timeout, ct);
        return ok
            ? new ActionResult(true, $"App pool '{name}' restarted successfully")
            : new ActionResult(false, $"Failed to restart app pool '{name}'");
    }
}