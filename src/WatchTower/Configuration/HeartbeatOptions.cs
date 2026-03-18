namespace WatchTower.Configuration;

public class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    public string Url { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; } = 1800;
}
