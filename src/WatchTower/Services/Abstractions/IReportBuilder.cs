using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface IReportBuilder
{
    ServerReport BuildReport(TimeSpan period);
    string RenderScheduledReportHtml(ServerReport report);
    string RenderEmergencyReportHtml(ServerReport report, Incident emergencyIncident);
}