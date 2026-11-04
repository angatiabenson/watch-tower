using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services.Alerting;

/// <summary>Email channel: the SMTP settings and recipients of the <c>Email</c> section.</summary>
public sealed class EmailNotificationChannel(
    IEmailService email,
    IReportBuilder reportBuilder,
    IOptionsMonitor<AlertingOptions> options) : INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public bool IsAvailable(NotificationChannel channel) => email.IsConfigured;

    public async Task<ChannelSendResult> SendAsync(NotificationChannel channel, AlertNotification notification, CancellationToken ct)
    {
        var sent = await email.SendEmailAsync(Subject(notification), Render(notification), ct);
        return sent
            ? ChannelSendResult.Ok("Email sent")
            : ChannelSendResult.Failed("The email could not be sent; check the SMTP settings and the log");
    }

    private string Render(AlertNotification n)
    {
        // The full 24 h report is opt-in: it is expensive to build and buries the alert.
        if (options.CurrentValue.IncludeFullReport && n.Incident is { } incident &&
            n.Kind is AlertNotificationKind.Opened or AlertNotificationKind.Reminder)
        {
            var report = reportBuilder.BuildReport(TimeSpan.FromHours(24));
            return reportBuilder.RenderEmergencyReportHtml(report, incident);
        }
        return reportBuilder.RenderAlertHtml(n);
    }

    public static string Subject(AlertNotification n) => n.Kind switch
    {
        AlertNotificationKind.Reminder => $"[WatchTower] Still open: {n.Title} ({n.Server})",
        AlertNotificationKind.Resolved => $"[WatchTower] Resolved: {n.Title} ({n.Server})",
        AlertNotificationKind.Test => $"[WatchTower] Test notification ({n.Server})",
        _ => $"[WatchTower] {n.Title} ({n.Server})",
    };
}