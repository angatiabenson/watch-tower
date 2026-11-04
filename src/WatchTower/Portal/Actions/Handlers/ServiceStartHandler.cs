using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class ServiceStartHandler : IActionHandler
{
    private readonly IWindowsServiceManager _serviceManager;
    private readonly ILogger<ServiceStartHandler> _logger;

    public ActionType ActionType => ActionType.ServiceStart;

    public ServiceStartHandler(
        IWindowsServiceManager serviceManager,
        ILogger<ServiceStartHandler> logger)
    {
        _serviceManager = serviceManager;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!TryGetServiceName(parameters, out var name))
            return Task.FromResult(new ActionResult(false, "Missing or invalid ServiceName parameter"));

        var timeout = GetTimeout(parameters);
        _logger.LogInformation("Portal command: starting service '{Name}'", name);

        var status = _serviceManager.GetServiceStatus(name);
        if (status.IsRunning)
            return Task.FromResult(new ActionResult(true, $"Service '{name}' is already running"));

        var ok = _serviceManager.RestartService(name, timeout);
        return Task.FromResult(ok
            ? new ActionResult(true, $"Service '{name}' started successfully")
            : new ActionResult(false, $"Failed to start service '{name}'"));
    }

    internal static bool TryGetServiceName(
        IReadOnlyDictionary<string, string> parameters, out string name)
    {
        name = string.Empty;
        if (!parameters.TryGetValue("ServiceName", out var raw) ||
            string.IsNullOrWhiteSpace(raw)) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(raw, @"^[A-Za-z0-9_\-\. ]{1,260}$"))
            return false;
        name = raw.Trim();
        return true;
    }

    internal static TimeSpan GetTimeout(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("TimeoutSeconds", out var raw) &&
            int.TryParse(raw, out var secs) && secs is >= 1 and <= 600)
            return TimeSpan.FromSeconds(secs);
        return TimeSpan.FromSeconds(30);
    }
}