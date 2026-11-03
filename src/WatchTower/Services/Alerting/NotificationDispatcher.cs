using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Fans a notification out to every enabled channel whose <c>MinSeverity</c> it meets. Channels
/// are sent in parallel, each with a short timeout; one failing or slow channel never blocks or
/// fails the others. Logs name the channel and its type, never its URL or secret.
/// </summary>
public sealed class NotificationDispatcher(
    IEnumerable<INotificationChannel> channels,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    private readonly Dictionary<NotificationChannelType, INotificationChannel> _senders =
        channels.ToDictionary(c => c.Type);

    public async Task<int> DispatchAsync(AlertNotification notification, CancellationToken ct)
    {
        var targets = Eligible(notification.Severity).ToList();
        if (targets.Count == 0)
        {
            logger.LogDebug("No notification channel accepts {Severity} {Kind} for {Key}",
                notification.Severity, notification.Kind, notification.AlertKey);
            return 0;
        }

        await Task.WhenAll(targets.Select(t => SendAsync(t.Channel, t.Sender, notification, ct)));
        return targets.Count;
    }

    /// <summary>
    /// Sends a test message to one channel (by name) or to every enabled channel, ignoring
    /// severity filters. Returns one result per channel; empty when none matched.
    /// </summary>
    public async Task<IReadOnlyList<ChannelTestResult>> SendTestAsync(string? channelName, string server, CancellationToken ct)
    {
        var configured = NotificationChannelResolver.Resolve(options.CurrentValue)
            .Where(c => channelName is null || string.Equals(c.Name, channelName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var notification = new AlertNotification(
            AlertNotificationKind.Test, "test", AlertSeverity.Info, "Test notification",
            $"This is a test message from WatchTower {AgentInfo.Version} on {server}. " +
            "If you can read it, this notification channel works.",
            server, DateTime.UtcNow);

        var results = await Task.WhenAll(configured.Select(async channel =>
        {
            if (!_senders.TryGetValue(channel.Type, out var sender))
                return new ChannelTestResult(channel.Name, channel.Type.ToString(), false, "Channel type is not supported");
            if (!sender.IsAvailable(channel))
                return new ChannelTestResult(channel.Name, channel.Type.ToString(), false, NotAvailableReason(channel));
            var result = await SendAsync(channel, sender, notification, ct);
            return new ChannelTestResult(channel.Name, channel.Type.ToString(), result.Success, result.Message);
        }));
        return results;
    }

    private IEnumerable<(NotificationChannel Channel, INotificationChannel Sender)> Eligible(AlertSeverity severity)
    {
        foreach (var channel in NotificationChannelResolver.Resolve(options.CurrentValue))
        {
            if (severity < channel.MinSeverity)
                continue;
            if (!_senders.TryGetValue(channel.Type, out var sender) || !sender.IsAvailable(channel))
                continue;
            yield return (channel, sender);
        }
    }

    private async Task<ChannelSendResult> SendAsync(
        NotificationChannel channel, INotificationChannel sender, AlertNotification notification, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.TimeoutSeconds, 1, 120));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Email has its own SMTP timeout (Email:TimeoutSeconds); webhooks get the short one.
        if (channel.Type != NotificationChannelType.Email)
            cts.CancelAfter(timeout);

        ChannelSendResult result;
        try
        {
            result = await sender.SendAsync(channel, notification, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ChannelSendResult.Failed($"Timed out after {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages may contain the endpoint; log the type only.
            result = ChannelSendResult.Failed($"Failed ({ex.GetType().Name})");
        }

        if (result.Success)
            logger.LogInformation("{Kind} notification for {Key} sent via {Channel} ({Type})",
                notification.Kind, notification.AlertKey, channel.Name, channel.Type);
        else
            logger.LogWarning("{Kind} notification for {Key} via {Channel} ({Type}) failed: {Reason}",
                notification.Kind, notification.AlertKey, channel.Name, channel.Type, result.Message);
        return result;
    }

    private static string NotAvailableReason(NotificationChannel channel) => channel.Type == NotificationChannelType.Email
        ? "Email is not configured (SMTP host, sender and at least one recipient are required)"
        : "The webhook URL is not a valid http(s) URL";
}