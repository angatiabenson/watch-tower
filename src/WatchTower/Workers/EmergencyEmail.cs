using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

/// <summary>
/// Shared emergency-alert email path for the monitoring workers. The caller records the
/// incident and starts its cooldown first; this only builds and sends the email, and skips the
/// (expensive) 24h report entirely when SMTP is not configured.
/// </summary>
internal static class EmergencyEmail
{
    public static async Task<bool> TrySendAsync(
        IEmailService emailService,
        IReportBuilder reportBuilder,
        Incident incident,
        string subject,
        ILogger logger,
        CancellationToken ct)
    {
        if (!emailService.IsConfigured)
        {
            logger.LogDebug("SMTP is not configured; emergency email skipped: {Subject}", subject);
            return false;
        }

        var report = reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = reportBuilder.RenderEmergencyReportHtml(report, incident);
        var sent = await emailService.SendEmailAsync(subject, html, ct);

        if (!sent)
        {
            // The incident is already recorded and published to the portal, and the cooldown
            // is running, so the next alert for this condition waits for the cooldown instead
            // of re-recording an incident on every poll while SMTP is down.
            logger.LogWarning("Emergency email could not be delivered: {Subject}", subject);
        }

        return sent;
    }
}