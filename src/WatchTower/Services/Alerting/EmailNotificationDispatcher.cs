using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services.Alerting;

/// <summary>Delivers alert notifications by email (SMTP via <see cref="IEmailService"/>).</summary>
public sealed class EmailNotificationDispatcher(
    IEmailService email,
    IReportBuilder reportBuilder,
    IOptionsMonitor<AlertingOptions> options,
    ILogger<EmailNotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task<int> DispatchAsync(AlertNotification notification, CancellationToken ct)
    {
        if (!email.IsConfigured)
        {
            logger.LogDebug("SMTP is not configured; {Kind} email for {Key} skipped", notification.Kind, notification.AlertKey);
            return 0;
        }

        var subject = Subject(notification);
        var html = Render(notification);
        if (!await email.SendEmailAsync(subject, html, ct))
            logger.LogWarning("Alert email could not be delivered: {Subject}", subject);
        return 1;
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