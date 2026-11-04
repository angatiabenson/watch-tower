using WatchTower.Services.Alerting;

namespace WatchTower.Portal.Actions.Handlers;

/// <summary>
/// Sends a test message through every enabled notification channel, or only through the one
/// named by the optional <c>Channel</c> parameter. Severity filters and maintenance windows do
/// not apply. The result lists each channel's outcome (never its URL).
/// </summary>
public class SendTestNotificationHandler(NotificationDispatcher notifications, ILogger<SendTestNotificationHandler> logger)
    : IActionHandler
{
    public ActionType ActionType => ActionType.SendTestNotification;

    public async Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var channel = parameters.TryGetValue("Channel", out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        logger.LogInformation("Portal command: send test notification ({Channel})", channel ?? "all channels");

        var results = await notifications.SendTestAsync(channel, Environment.MachineName, ct);
        if (results.Count == 0)
        {
            return new ActionResult(false, channel is null
                ? "No notification channel is enabled"
                : $"No enabled notification channel is named '{channel}'");
        }

        var summary = string.Join("; ", results.Select(r => $"{r.Channel} ({r.Type}): {(r.Ok ? "ok" : r.Message)}"));
        return new ActionResult(results.All(r => r.Ok), summary);
    }
}