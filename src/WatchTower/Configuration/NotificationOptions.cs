namespace WatchTower.Configuration;

/// <summary>
/// Where alert notifications go. Section <c>Notifications</c>.
/// </summary>
/// <remarks>
/// The common case is configured with fixed keys the web console can edit: <see cref="Email"/>,
/// <see cref="Teams"/>, <see cref="Slack"/> and <see cref="Webhook"/> (each enabled once it has a
/// URL). Additional or differently-filtered channels go in <see cref="Channels"/>, a list that is
/// set with <c>WatchTower config import</c>; an entry whose <c>Name</c> matches a fixed channel
/// (<c>email</c>, <c>teams</c>, <c>slack</c>, <c>webhook</c>) replaces it. Webhook URLs and signing
/// secrets are stored DPAPI-encrypted and never returned by the console API.
/// </remarks>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    public EmailNotificationSettings Email { get; set; } = new();
    public WebhookNotificationSettings Teams { get; set; } = new();
    public WebhookNotificationSettings Slack { get; set; } = new();
    public WebhookNotificationSettings Webhook { get; set; } = new();

    /// <summary>Additional channels (list of objects; set via <c>config import</c>).</summary>
    public List<NotificationChannelOptions> Channels { get; set; } = [];

    /// <summary>Per-channel delivery timeout for webhook-style channels.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// While a channel is backing off after failures, a new critical alert (or its recovery) may
    /// probe it early, at most once this often per channel (60–3600, default 120: at most 30 extra
    /// attempts an hour to a channel that stays down, and only while critical alerts keep coming).
    /// </summary>
    public int CriticalProbeCooldownSeconds { get; set; } = 120;
}

public class EmailNotificationSettings
{
    /// <summary>Send alerts to the <c>Email:ToAddresses</c> recipients (when SMTP is configured).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Info | Warning | Critical.</summary>
    public string MinSeverity { get; set; } = "Info";
}

public class WebhookNotificationSettings
{
    /// <summary>Incoming-webhook URL; the channel is enabled once this is set.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 signing secret (generic webhook only).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Info | Warning | Critical.</summary>
    public string MinSeverity { get; set; } = "Info";
}

/// <summary>One entry of <c>Notifications:Channels</c>.</summary>
public class NotificationChannelOptions
{
    /// <summary>Unique name, used in logs and by the test action. Defaults to "&lt;type&gt;-&lt;index&gt;".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email | Webhook | Teams | Slack.</summary>
    public string Type { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 signing secret (Webhook only).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Info | Warning | Critical.</summary>
    public string MinSeverity { get; set; } = "Info";

    public bool Enabled { get; set; } = true;
}