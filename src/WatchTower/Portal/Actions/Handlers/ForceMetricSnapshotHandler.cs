namespace WatchTower.Portal.Actions.Handlers;

public class ForceMetricSnapshotHandler : IActionHandler
{
    private readonly IMetricSnapshotTrigger _trigger;
    private readonly ILogger<ForceMetricSnapshotHandler> _logger;

    public ActionType ActionType => ActionType.ForceMetricSnapshot;

    public ForceMetricSnapshotHandler(
        IMetricSnapshotTrigger trigger,
        ILogger<ForceMetricSnapshotHandler> logger)
    {
        _trigger = trigger;
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        _logger.LogInformation("Portal command: force metric snapshot");
        _trigger.Signal();
        return Task.FromResult(new ActionResult(true, "Metric snapshot triggered"));
    }
}
