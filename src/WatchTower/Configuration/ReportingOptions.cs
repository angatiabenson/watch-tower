namespace WatchTower.Configuration;

public class ReportingOptions
{
    public const string SectionName = "Reporting";

    public string DailyReportTime { get; set; } = "08:00";
    /// <summary>
    /// No longer used: alert repetition is governed by <c>Alerting:RenotifyMinutes</c>. Kept so
    /// existing settings files still bind.
    /// </summary>
    public int EmergencyCooldownSeconds { get; set; } = 300;
}