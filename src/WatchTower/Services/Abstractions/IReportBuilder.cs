using WatchTower.Models;
using WatchTower.Services.Alerting;

namespace WatchTower.Services.Abstractions;

public interface IReportBuilder
{
    ServerReport BuildReport(TimeSpan period);
    string RenderScheduledReportHtml(ServerReport report);
    string RenderEmergencyReportHtml(ServerReport report, Incident emergencyIncident);

    /// <summary>Renders the short alert email (no 24-hour report) in the display time zone.</summary>
    string RenderAlertHtml(AlertNotification notification);
}