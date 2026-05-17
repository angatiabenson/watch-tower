using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class ServiceRestartHandler : IActionHandler
{
    private readonly IWindowsServiceManager _serviceManager;
    private readonly ILogger<ServiceRestartHandler> _logger;

    public ActionType ActionType => ActionType.ServiceRestart;

    public ServiceRestartHandler(
        IWindowsServiceManager serviceManager,
        ILogger<ServiceRestartHandler> logger)
    {
        _serviceManager = serviceManager;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!ServiceStartHandler.TryGetServiceName(parameters, out var name))
            return Task.FromResult(new ActionResult(false, "Missing or invalid ServiceName parameter"));

        var timeout = ServiceStartHandler.GetTimeout(parameters);
        _logger.LogInformation("Portal command: restarting service '{Name}'", name);

        var ok = _serviceManager.RestartService(name, timeout);
        return Task.FromResult(ok
            ? new ActionResult(true, $"Service '{name}' restarted successfully")
            : new ActionResult(false, $"Failed to restart service '{name}'"));
    }
}
