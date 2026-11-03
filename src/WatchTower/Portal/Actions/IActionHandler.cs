namespace WatchTower.Portal.Actions;

public interface IActionHandler
{
    ActionType ActionType { get; }
    Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

public record ActionResult(bool Success, string Message);