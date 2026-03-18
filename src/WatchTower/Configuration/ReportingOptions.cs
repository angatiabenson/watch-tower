namespace WatchTower.Configuration;

public class ReportingOptions
{
    public const string SectionName = "Reporting";

    public int ReportIntervalSeconds { get; set; } = 7200;
    public int EmergencyCooldownSeconds { get; set; } = 300;
}
