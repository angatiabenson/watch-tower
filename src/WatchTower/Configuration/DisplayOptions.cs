namespace WatchTower.Configuration;

public class DisplayOptions
{
    public const string SectionName = "Display";

    public string Timezone { get; set; } = "Africa/Nairobi";

    /// <summary>Short label appended to rendered timestamps (e.g. "EAT", "UTC"). Should match <see cref="Timezone"/>.</summary>
    public string TimezoneLabel { get; set; } = "EAT";
}
