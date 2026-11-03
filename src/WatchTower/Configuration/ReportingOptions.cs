namespace WatchTower.Configuration;

public class ReportingOptions
{
    public const string SectionName = "Reporting";

    public string DailyReportTime { get; set; } = "08:00";
    public int EmergencyCooldownSeconds { get; set; } = 300;
}