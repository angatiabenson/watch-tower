namespace WatchTower.Updates;

/// <summary>Self-update settings (section <c>Updates</c>).</summary>
public class UpdateOptions
{
    public const string SectionName = "Updates";
    public const string DefaultFeedUrl = "https://api.github.com/repos/angatiabenson/watch-tower/releases";

    public bool Enabled { get; set; } = true;

    /// <summary><c>stable</c> or <c>prerelease</c>.</summary>
    public string Channel { get; set; } = "stable";

    /// <summary>
    /// Where to look for releases: a GitHub "releases" API URL (each release must carry a
    /// <c>watchtower-update.json</c> asset), or the URL of a manifest JSON file for self-hosted
    /// distribution. HTTPS only.
    /// </summary>
    public string FeedUrl { get; set; } = DefaultFeedUrl;

    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>Download new versions in the background (installing always needs a click or a portal command).</summary>
    public bool AutoDownload { get; set; }

    /// <summary>Countdown between a finished download and the automatic restart that applies it.</summary>
    public int RestartCountdownSeconds { get; set; } = 30;

    /// <summary>
    /// Optional ECDSA P-256 public key (PEM). When set, every update package must carry a valid
    /// signature from the matching private key, otherwise it is rejected.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// When true (default) the feed and packages must be fetched over HTTPS. Turn off to use a
    /// plain-HTTP feed (e.g. an internal file server); set <see cref="PublicKey"/> as well so a
    /// package altered in transit is still rejected.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>How long to wait for the new version to report healthy before rolling back.</summary>
    public int HealthTimeoutSeconds { get; set; } = 120;
}