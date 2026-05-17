using WatchTower.Services.Abstractions;

namespace WatchTower.Portal.Actions.Handlers;

public class AppPoolStartHandler : IActionHandler
{
    private readonly IIisApplicationPoolManager _poolManager;
    private readonly ILogger<AppPoolStartHandler> _logger;

    public ActionType ActionType => ActionType.AppPoolStart;

    public AppPoolStartHandler(
        IIisApplicationPoolManager poolManager,
        ILogger<AppPoolStartHandler> logger)
    {
        _poolManager = poolManager;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!TryGetPoolName(parameters, out var name))
            return Task.FromResult(new ActionResult(false, "Missing or invalid AppPoolName parameter"));

        var timeout = GetTimeout(parameters);
        _logger.LogInformation("Portal command: starting app pool '{Name}'", name);

        var ok = _poolManager.StartApplicationPool(name, timeout);
        return Task.FromResult(ok
            ? new ActionResult(true, $"App pool '{name}' started successfully")
            : new ActionResult(false, $"Failed to start app pool '{name}'"));
    }

    internal static bool TryGetPoolName(
        IReadOnlyDictionary<string, string> parameters, out string name)
    {
        name = string.Empty;
        if (!parameters.TryGetValue("AppPoolName", out var raw) ||
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
