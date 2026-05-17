using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class ServiceStopHandler : IActionHandler
{
    private readonly IWindowsServiceManager _serviceManager;
    private readonly ILogger<ServiceStopHandler> _logger;

    public ActionType ActionType => ActionType.ServiceStop;

    public ServiceStopHandler(
        IWindowsServiceManager serviceManager,
        ILogger<ServiceStopHandler> logger)
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
        _logger.LogInformation("Portal command: stopping service '{Name}'", name);

        var ok = _serviceManager.StopService(name, timeout);
        return Task.FromResult(ok
            ? new ActionResult(true, $"Service '{name}' stopped successfully")
            : new ActionResult(false, $"Failed to stop service '{name}'"));
    }
}
