using System.Text.Json.Serialization;

namespace WatchTower.Ui;

/// <summary>Kinds of editor the UI renders for a field.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    String,
    /// <summary>Write-only secret: the API never returns its value, only whether one is set.</summary>
    Secret,
    Int,
    Number,
    Bool,
    /// <summary>One of <see cref="ConfigField.Options"/>.</summary>
    Enum,
    /// <summary>List of strings (optionally validated by <see cref="ConfigField.Format"/>).</summary>
    List,
    /// <summary>Time of day, HH:mm.</summary>
    Time,
    /// <summary>IANA or Windows time zone id; options from <c>/api/system/timezones</c>.</summary>
    TimeZone,
    /// <summary>List of Windows service names; picker data from <c>/api/system/services</c>.</summary>
    Services,
    /// <summary>List of IIS application pool names; picker data from <c>/api/system/apppools</c>.</summary>
    AppPools,
    Url,
}

public sealed record ConfigField(
    string Key,
    string Label,
    FieldType Type,
    string? Help = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Options = null,
    string? Placeholder = null,
    bool RestartRequired = false,
    /// <summary>Extra validation for strings / list items: "email" | "url".</summary>
    string? Format = null,
    /// <summary>Rendered in an "Advanced" disclosure.</summary>
    bool Advanced = false);

public sealed record ConfigSection(string Id, string Title, string Description, IReadOnlyList<ConfigField> Fields);

/// <summary>
/// Everything the configuration UI can edit. The UI is rendered entirely from this schema,
/// and the API only accepts keys listed here — so adding a setting is a one-line change.
/// </summary>
public static class ConfigSchema
{
    private static readonly IReadOnlyList<string> SeverityOptions = ["Info", "Warning", "Critical"];

    public static IReadOnlyList<ConfigSection> Sections { get; } =
    [
        new("general", "General", "Reporting schedule and how times are displayed.",
        [
            new("Reporting:DailyReportTime", "Daily report time", FieldType.Time,
                "Local time (in the display time zone) at which the daily summary email is sent."),
            new("Display:Timezone", "Display time zone", FieldType.TimeZone,
                "Time zone used for the report schedule and all timestamps in emails."),
            new("Display:TimezoneLabel", "Time zone label", FieldType.String,
                "Short label shown after timestamps, e.g. EAT or UTC.", Placeholder: "UTC"),
        ]),

        new("alerting", "Alerting", "When operators are notified about a problem, and how often.",
        [
            new("Alerting:RenotifyMinutes", "Remind every (min)", FieldType.Int,
                "While an alert stays open, send a reminder this often. 0 = notify once per occurrence.", 0, 10_080),
            new("Alerting:SendRecovery", "Notify when resolved", FieldType.Bool,
                "Send a \"resolved\" message (with how long it lasted) when an alert clears."),
            new("Alerting:IncludeFullReport", "Attach full server report to alert emails", FieldType.Bool,
                "Off: alert emails are short. On: they also contain the 24-hour server report."),
            new("Alerting:ResolveAfterSeconds", "Resolve after clear for (s)", FieldType.Int,
                "CPU, memory, disk, disk-queue and network alerts resolve only once the condition stayed clear this long, so values hovering around a threshold do not flap.",
                0, 86_400, Advanced: true),
        ]),

        new("maintenance", "Maintenance window",
            "While a window is active, incidents are still recorded but notifications are held back and " +
            "services / application pools are not restarted automatically. Still-open alerts notify once it ends.",
        [
            new("Maintenance:Until", "Maintenance until (UTC)", FieldType.String,
                "ISO-8601 UTC end time, e.g. 2026-11-03T18:00:00Z. Empty or in the past = no maintenance. " +
                "'WatchTower maintenance start --minutes 60' sets it for you.",
                Placeholder: "2026-11-03T18:00:00Z"),
            new("Maintenance:Reason", "Reason", FieldType.String, "Shown in the console and the portal.",
                Placeholder: "Monthly patching"),
        ]),

        new("notifications", "Notification channels",
            "Where alerts are sent besides email. Webhook URLs are stored encrypted (Windows DPAPI). " +
            "More channels, or several of one kind, can be added with 'WatchTower config import' (Notifications:Channels).",
        [
            new("Notifications:Email:Enabled", "Send alerts by email", FieldType.Bool,
                "Uses the SMTP settings and recipients of the Email alerts section."),
            new("Notifications:Email:MinSeverity", "Email: minimum severity", FieldType.Enum, Options: SeverityOptions),
            new("Notifications:Teams:Url", "Microsoft Teams webhook URL", FieldType.Secret,
                "A Teams Workflows \"Post to a channel when a webhook request is received\" URL. Alerts are posted as Adaptive Cards.",
                Format: "url"),
            new("Notifications:Teams:MinSeverity", "Teams: minimum severity", FieldType.Enum, Options: SeverityOptions),
            new("Notifications:Slack:Url", "Slack incoming webhook URL", FieldType.Secret,
                "e.g. https://hooks.slack.com/services/…", Format: "url"),
            new("Notifications:Slack:MinSeverity", "Slack: minimum severity", FieldType.Enum, Options: SeverityOptions),
            new("Notifications:Webhook:Url", "Webhook URL", FieldType.Secret,
                "Receives a JSON document per notification (schema watchtower.alert.v1).", Format: "url"),
            new("Notifications:Webhook:Secret", "Webhook signing secret", FieldType.Secret,
                "When set, each request carries X-WatchTower-Signature: sha256=<HMAC-SHA256 of the body>."),
            new("Notifications:Webhook:MinSeverity", "Webhook: minimum severity", FieldType.Enum, Options: SeverityOptions),
            new("Notifications:TimeoutSeconds", "Webhook timeout (s)", FieldType.Int, null, 1, 120, Advanced: true),
        ]),

        new("email", "Email alerts", "SMTP server and recipients for daily reports and emergency alerts.",
        [
            new("Email:SmtpHost", "SMTP host", FieldType.String, "Leave empty to disable email.",
                Placeholder: "smtp.office365.com"),
            new("Email:SmtpPort", "SMTP port", FieldType.Int, null, 1, 65_535),
            new("Email:Security", "Connection security", FieldType.Enum,
                "StartTls for port 587, Ssl for port 465, None only for trusted internal relays.",
                Options: ["StartTls", "Ssl", "None", "Auto"]),
            new("Email:Username", "Username", FieldType.String, "Leave empty for unauthenticated relays."),
            new("Email:Password", "Password", FieldType.Secret, "Stored encrypted (Windows DPAPI)."),
            new("Email:FromAddress", "From address", FieldType.String, Format: "email",
                Placeholder: "watchtower@example.com"),
            new("Email:FromName", "From name", FieldType.String),
            new("Email:ToAddresses", "Recipients", FieldType.List, "One email address per entry.", Format: "email"),
            new("Email:TimeoutSeconds", "SMTP timeout (s)", FieldType.Int, null, 1, 600, Advanced: true),
        ]),

        new("cpu", "CPU", "Alert when processor usage stays above a threshold.",
        [
            new("Monitoring:Cpu:ThresholdPercent", "Alert threshold (%)", FieldType.Number, null, 1, 100),
            new("Monitoring:Cpu:SustainedSeconds", "Sustained for (s)", FieldType.Int,
                "Usage must stay above the threshold this long before an emergency alert. Shorter bursts are logged as spikes.",
                0, 86_400),
            new("Monitoring:Cpu:PollIntervalSeconds", "Sample every (s)", FieldType.Int, null, 1, 3_600),
            new("Monitoring:Cpu:TopProcessCount", "Top processes in alerts", FieldType.Int, null, 0, 20, Advanced: true),
        ]),

        new("ram", "Memory", "Alert when memory usage stays above a threshold.",
        [
            new("Monitoring:Ram:ThresholdPercent", "Alert threshold (%)", FieldType.Number, null, 1, 100),
            new("Monitoring:Ram:SustainedSeconds", "Sustained for (s)", FieldType.Int, null, 0, 86_400),
            new("Monitoring:Ram:PollIntervalSeconds", "Sample every (s)", FieldType.Int, null, 1, 3_600),
            new("Monitoring:Ram:TopProcessCount", "Top processes in alerts", FieldType.Int, null, 0, 20, Advanced: true),
        ]),

        new("disk", "Disks", "Free space and I/O queue depth per drive.",
        [
            new("Monitoring:Disk:WarningThresholdPercent", "Warning at (% used)", FieldType.Number, null, 1, 100),
            new("Monitoring:Disk:CriticalThresholdPercent", "Critical alert at (% used)", FieldType.Number, null, 1, 100),
            new("Monitoring:Disk:QueueLengthWarningThreshold", "I/O queue alert threshold", FieldType.Number,
                "SSD/NVMe: ~2. Spinning disks: 4–5.", 0, 1_000),
            new("Monitoring:Disk:PollIntervalSeconds", "Check every (s)", FieldType.Int, null, 1, 3_600),
        ]),

        new("network", "Network", "Alert when a network adapter's output queue backs up.",
        [
            new("Monitoring:Network:OutputQueueWarningThreshold", "Output queue alert threshold", FieldType.Number,
                "0 is right for most servers: any queued packets mean the NIC is saturated.", 0, 10_000),
            new("Monitoring:Network:PollIntervalSeconds", "Check every (s)", FieldType.Int, null, 1, 3_600),
        ]),

        new("services", "Windows services", "Services to watch and automatically restart.",
        [
            new("Monitoring:Services:WatchedServices", "Watched services", FieldType.Services,
                "Stopped services are restarted automatically; an alert is sent if recovery fails."),
            new("Monitoring:Services:MaxRestartAttempts", "Max restart attempts", FieldType.Int, null, 0, 50),
            new("Monitoring:Services:RestartDelaySeconds", "Delay before restart (s)", FieldType.Int, null, 0, 3_600),
            new("Monitoring:Services:PollIntervalSeconds", "Check every (s)", FieldType.Int, null, 1, 3_600),
            new("Monitoring:Services:RestartTimeoutSeconds", "Restart timeout (s)", FieldType.Int, null, 1, 3_600, Advanced: true),
            new("Monitoring:Services:StableStateDelaySeconds", "Stability check after restart (s)", FieldType.Int,
                null, 0, 600, Advanced: true),
            new("Monitoring:Services:StartupGraceSeconds", "Grace period after boot (s)", FieldType.Int,
                "Right after WatchTower starts, stopped services are recorded but not restarted or alerted on.",
                0, 3_600, Advanced: true),
        ]),

        new("apppools", "IIS application pools", "Application pools to watch and automatically start.",
        [
            new("Monitoring:ApplicationPools:WatchedApplicationPools", "Watched application pools", FieldType.AppPools),
            new("Monitoring:ApplicationPools:MaxStartAttempts", "Max start attempts", FieldType.Int, null, 0, 50),
            new("Monitoring:ApplicationPools:StartDelaySeconds", "Delay before start (s)", FieldType.Int, null, 0, 3_600),
            new("Monitoring:ApplicationPools:PollIntervalSeconds", "Check every (s)", FieldType.Int, null, 1, 3_600),
            new("Monitoring:ApplicationPools:StartTimeoutSeconds", "Start timeout (s)", FieldType.Int, null, 1, 3_600, Advanced: true),
            new("Monitoring:ApplicationPools:StableStateDelaySeconds", "Stability check after start (s)", FieldType.Int,
                null, 0, 600, Advanced: true),
        ]),

        new("heartbeat", "Dead man's switch", "Ping an external monitor (e.g. healthchecks.io) so you hear about it if WatchTower itself stops.",
        [
            new("Heartbeat:Url", "Ping URL", FieldType.Secret,
                "e.g. https://hc-ping.com/<uuid>. Stored encrypted: anyone with the URL can forge pings.",
                Format: "url"),
            new("Heartbeat:IntervalSeconds", "Ping every (s)", FieldType.Int, null, 60, 86_400),
        ]),

        new("portal", "Visibility Portal", "Stream metrics and incidents to a central portal and accept remote commands.",
        [
            new("VisibilityPortal:Enabled", "Connect to a portal", FieldType.Bool, RestartRequired: true),
            new("VisibilityPortal:BaseUrl", "Portal URL", FieldType.Url, Placeholder: "https://portal.example.com",
                RestartRequired: true),
            new("VisibilityPortal:AgentId", "Agent ID", FieldType.String, "Identifier issued by the portal for this server."),
            new("VisibilityPortal:AgentToken", "Agent token", FieldType.Secret, "Stored encrypted (Windows DPAPI)."),
            new("VisibilityPortal:Protocol", "Protocol", FieldType.Enum, Options: ["Https", "Grpc"], RestartRequired: true),
            new("VisibilityPortal:RemoteConfig:Enabled", "Allow the portal to manage settings", FieldType.Bool,
                "Portal-managed values override the ones set here.", RestartRequired: true),
            new("VisibilityPortal:Tls:RequireTls", "Require HTTPS (recommended)", FieldType.Bool,
                "Turn off to connect to a portal at an http:// address, e.g. on a trusted internal network. " +
                "The agent token, metrics and commands then travel unencrypted."),
            new("VisibilityPortal:Outbox:FlushIntervalSeconds", "Send metrics every (s)", FieldType.Int, null, 1, 3_600, Advanced: true),
            new("VisibilityPortal:CommandPoll:PollIntervalSeconds", "Poll for commands every (s)", FieldType.Int, null, 1, 3_600, Advanced: true),
            new("VisibilityPortal:RemoteConfig:PollIntervalSeconds", "Fetch remote settings every (s)", FieldType.Int, null, 1, 86_400, Advanced: true),
            new("VisibilityPortal:CommandPoll:MaxCommandAgeSeconds", "Reject commands older than (s)", FieldType.Int,
                "Replay protection. 0 disables.", 0, 86_400, Advanced: true),
            new("VisibilityPortal:CommandPoll:AllowPortalManagedTargets", "Let the portal choose command targets", FieldType.Bool,
                "Off: portal commands may only start/stop services and pools watched in THIS server's settings. On: lists pushed by the portal count too.",
                Advanced: true),
            new("VisibilityPortal:CommandPoll:RetentionDays", "Keep command history (days)", FieldType.Int,
                "0 keeps it forever.", 0, 3_650, Advanced: true),
            new("VisibilityPortal:Outbox:EventMaxAgeHours", "Keep undelivered incidents for (hours)", FieldType.Int,
                "Incidents waiting for an unreachable portal are dropped after this long.", 1, 8_760, Advanced: true),
            new("VisibilityPortal:Tls:ClientCertThumbprint", "Client certificate thumbprint (mTLS)", FieldType.String,
                @"Certificate in LocalMachine\My presented to the portal.", RestartRequired: true, Advanced: true),
        ]),

        new("ui", "Web console", "Where this configuration console listens.",
        [
            new("Ui:SessionTimeoutMinutes", "Sign-out after idle (min)", FieldType.Int, null, 5, 1_440, RestartRequired: true),
            new("Ui:Urls", "Listen URLs", FieldType.List,
                "Default http://127.0.0.1:8750 (this server only). Non-local addresses need remote access enabled, " +
                "and https:// with a certificate unless HTTPS is no longer required below.",
                Format: "url", RestartRequired: true, Advanced: true),
            new("Ui:AllowRemoteAccess", "Allow remote access", FieldType.Bool,
                "Expose the console beyond this server.", RestartRequired: true, Advanced: true),
            new("Ui:RequireHttpsForRemoteAccess", "Require HTTPS for remote access (recommended)", FieldType.Bool,
                "Turn off to serve the console to other machines over plain http://. The console password and " +
                "session then travel unencrypted.", RestartRequired: true, Advanced: true),
            new("Ui:AllowedHosts", "Allowed host names", FieldType.List,
                "Names other machines use to reach the console, e.g. srv-01.corp.local. Empty allows any.",
                Advanced: true),
            new("Ui:CertificateThumbprint", "HTTPS certificate thumbprint", FieldType.String,
                "Certificate in LocalMachine\\My used for https:// URLs.", RestartRequired: true, Advanced: true),
        ]),

        new("updates", "Updates", "How WatchTower checks for and installs new versions.",
        [
            new("Updates:Enabled", "Check for updates automatically", FieldType.Bool),
            new("Updates:Channel", "Release channel", FieldType.Enum,
                "Prerelease builds get features earlier but are less tested.", Options: ["stable", "prerelease"]),
            new("Updates:CheckIntervalHours", "Check every (hours)", FieldType.Int, null, 1, 168),
            new("Updates:AutoDownload", "Download new versions in the background", FieldType.Bool,
                "Installing always needs a click (or a portal command)."),
            new("Updates:RestartCountdownSeconds", "Restart countdown (s)", FieldType.Int,
                "Delay between a finished download and the restart that installs it.", 5, 3_600),
            new("Updates:FeedUrl", "Release feed", FieldType.Url,
                "A GitHub releases API URL or your own watchtower-update.json manifest URL.",
                Placeholder: Updates.UpdateOptions.DefaultFeedUrl, Advanced: true),
            new("Updates:RequireHttps", "Require HTTPS for downloads (recommended)", FieldType.Bool,
                "Turn off to use a plain http:// feed (e.g. an internal file server). Also set a signing key " +
                "below so tampered packages are still rejected.", Advanced: true),
            new("Updates:PublicKey", "Signing public key (PEM)", FieldType.String,
                "When set, only packages signed with the matching private key are installed.", Advanced: true),
        ]),

        new("logging", "Logging","Diagnostic log verbosity. Logs are kept for 30 days.",
        [
            new("Serilog:MinimumLevel:Default", "Log level", FieldType.Enum,
                Options: ["Verbose", "Debug", "Information", "Warning", "Error"], RestartRequired: true),
        ]),
    ];

    private static readonly Dictionary<string, ConfigField> ByKey = Sections
        .SelectMany(s => s.Fields)
        .ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static bool TryGetField(string key, out ConfigField field) => ByKey.TryGetValue(key, out field!);

    public static IEnumerable<ConfigField> AllFields => ByKey.Values;

    public static bool IsListType(FieldType type) => type is FieldType.List or FieldType.Services or FieldType.AppPools;
}