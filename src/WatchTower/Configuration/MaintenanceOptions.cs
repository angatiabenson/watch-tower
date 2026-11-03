namespace WatchTower.Configuration;

/// <summary>
/// Maintenance window. Section <c>Maintenance</c>. Set with <c>WatchTower maintenance start</c>,
/// the console or the portal (remote config <c>maintenance</c>, which takes precedence).
/// </summary>
public class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    /// <summary>ISO-8601 UTC end of the window (e.g. <c>2026-11-03T18:00:00Z</c>). Empty or past = none.</summary>
    public string Until { get; set; } = string.Empty;

    /// <summary>Why (shown in the console and sent to the portal).</summary>
    public string Reason { get; set; } = string.Empty;
}