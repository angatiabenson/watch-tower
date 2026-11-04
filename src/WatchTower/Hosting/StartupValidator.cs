using WatchTower.Configuration;
using WatchTower.Configuration.Layered;
using WatchTower.Services.Alerting;
using WatchTower.Ui;

namespace WatchTower.Hosting;

/// <summary>
/// Configuration rules. Used at boot (fatal errors stop the service instead of leaving a
/// "zombie" that busy-loops or error-spins) and by the configuration UI to vet a change
/// before it is saved.
/// </summary>
public static class StartupValidator
{
    public sealed record Report(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool IsValid => Errors.Count == 0;
    }

    public static Report Validate(IServiceProvider services) =>
        Validate(services.GetRequiredService<IConfiguration>());

    public static Report Validate(IConfiguration configuration)
    {
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>() ?? new T();

        var errors = new List<string>();
        var warnings = new List<string>();

        void RequirePositive(string name, int value)
        {
            if (value < 1) errors.Add($"{name} must be >= 1 (was {value}).");
        }

        var watchedServices = Bind<ServiceMonitorOptions>(ServiceMonitorOptions.SectionName);
        var pools = Bind<ApplicationPoolMonitorOptions>(ApplicationPoolMonitorOptions.SectionName);
        var heartbeat = Bind<HeartbeatOptions>(HeartbeatOptions.SectionName);
        var email = Bind<EmailOptions>(EmailOptions.SectionName);
        var portal = Bind<PortalOptions>(PortalOptions.SectionName);
        var disk = Bind<DiskMonitorOptions>(DiskMonitorOptions.SectionName);
        var display = Bind<DisplayOptions>(DisplayOptions.SectionName);
        var reporting = Bind<ReportingOptions>(ReportingOptions.SectionName);

        RequirePositive("Monitoring:Cpu:PollIntervalSeconds", Bind<CpuMonitorOptions>(CpuMonitorOptions.SectionName).PollIntervalSeconds);
        RequirePositive("Monitoring:Ram:PollIntervalSeconds", Bind<RamMonitorOptions>(RamMonitorOptions.SectionName).PollIntervalSeconds);
        RequirePositive("Monitoring:Disk:PollIntervalSeconds", disk.PollIntervalSeconds);
        RequirePositive("Monitoring:Network:PollIntervalSeconds", Bind<NetworkMonitorOptions>(NetworkMonitorOptions.SectionName).PollIntervalSeconds);
        RequirePositive("Monitoring:Services:PollIntervalSeconds", watchedServices.PollIntervalSeconds);
        RequirePositive("Monitoring:ApplicationPools:PollIntervalSeconds", pools.PollIntervalSeconds);
        RequirePositive("Heartbeat:IntervalSeconds", heartbeat.IntervalSeconds);

        if (disk.WarningThresholdPercent > disk.CriticalThresholdPercent)
            errors.Add("Monitoring:Disk:WarningThresholdPercent must not exceed CriticalThresholdPercent.");

        if (!TimeSpan.TryParse(reporting.DailyReportTime, System.Globalization.CultureInfo.InvariantCulture, out var reportTime) ||
            reportTime < TimeSpan.Zero || reportTime >= TimeSpan.FromDays(1))
            warnings.Add($"Reporting:DailyReportTime '{reporting.DailyReportTime}' is not a valid HH:mm time; 08:00 will be used.");

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(display.Timezone, out _))
            warnings.Add($"Display:Timezone '{display.Timezone}' is not a known time zone; UTC will be used.");

        if (portal.Enabled)
        {
            if (string.IsNullOrWhiteSpace(portal.AgentId))
                errors.Add("VisibilityPortal.AgentId is required when the portal is enabled.");
            if (string.IsNullOrWhiteSpace(portal.AgentToken))
                errors.Add("VisibilityPortal.AgentToken is required when the portal is enabled.");
            if (string.IsNullOrWhiteSpace(portal.BaseUrl))
                errors.Add("VisibilityPortal.BaseUrl is required when the portal is enabled.");
            else if (!Uri.TryCreate(portal.BaseUrl, UriKind.Absolute, out var portalUri))
                errors.Add($"VisibilityPortal.BaseUrl '{portal.BaseUrl}' is not an absolute URL.");
            else if (portal.Tls.RequireTls && portalUri.Scheme != Uri.UriSchemeHttps)
                errors.Add("VisibilityPortal.BaseUrl must use https:// while Tls.RequireTls is enabled " +
                           "(turn off \"Require HTTPS\" to connect over plain HTTP).");
            else if (portalUri.Scheme == Uri.UriSchemeHttp && !portalUri.IsLoopback)
                warnings.Add("The portal connection uses plain HTTP: the agent token, metrics and commands " +
                             "travel unencrypted. Use https:// unless the network is trusted.");
            RequirePositive("VisibilityPortal:Outbox:FlushIntervalSeconds", portal.Outbox.FlushIntervalSeconds);
            RequirePositive("VisibilityPortal:CommandPoll:PollIntervalSeconds", portal.CommandPoll.PollIntervalSeconds);
            RequirePositive("VisibilityPortal:RemoteConfig:PollIntervalSeconds", portal.RemoteConfig.PollIntervalSeconds);
        }

        if (string.IsNullOrWhiteSpace(email.SmtpHost))
            warnings.Add("SMTP host is not configured. Email notifications will be disabled.");
        else if (email.ToAddresses.Count == 0)
            warnings.Add("No email recipients configured. Alerts and reports will not be sent.");
        else if (string.IsNullOrWhiteSpace(email.FromAddress))
            warnings.Add("Email:FromAddress is not set. Alerts and reports will not be sent.");
        if (string.IsNullOrWhiteSpace(heartbeat.Url))
            warnings.Add("Heartbeat URL is not configured. Dead man's switch is disabled.");
        if (watchedServices.WatchedServices.Count == 0)
            warnings.Add("No watched services configured. Service monitoring will be idle.");
        if (pools.WatchedApplicationPools.Count == 0)
            warnings.Add("No watched IIS application pools configured. Application pool monitoring will be idle.");

        // UI binding problems never stop monitoring: the console is simply not started.
        var uiPlan = UiEndpointPlanner.Plan(Bind<UiOptions>(UiOptions.SectionName));
        foreach (var problem in uiPlan.Problems)
            warnings.Add($"Web console: {problem}");
        foreach (var endpoint in uiPlan.Endpoints.Where(e => !e.IsLoopback && !e.Https))
            warnings.Add($"Web console: {endpoint.Display} is reachable from the network over plain HTTP; " +
                         "the console password and session travel unencrypted.");

        errors.AddRange(Services.Endpoints.EndpointCheck.Validate(Bind<EndpointMonitorOptions>(EndpointMonitorOptions.SectionName).Endpoints));
        ValidateAlerting(Bind<AlertingOptions>(AlertingOptions.SectionName), errors);
        var maintenance = Bind<MaintenanceOptions>(MaintenanceOptions.SectionName);
        if (!string.IsNullOrWhiteSpace(maintenance.Until) && !MaintenanceState.TryParseUntil(maintenance.Until, out _))
            errors.Add("Maintenance:Until must be an ISO-8601 timestamp such as 2026-11-03T18:00:00Z (or empty).");
        ValidateNotifications(Bind<NotificationOptions>(NotificationOptions.SectionName), errors, warnings);

        var updates = Bind<Updates.UpdateOptions>(Updates.UpdateOptions.SectionName);
        if (!updates.RequireHttps && string.IsNullOrWhiteSpace(updates.PublicKey))
            warnings.Add("Updates may be downloaded over plain HTTP without a signing key (Updates:PublicKey): " +
                         "a package altered in transit would not be detected.");

        var layered = LayeredJsonConfigurationProvider.Find(configuration);
        foreach (var key in layered?.UndecryptableKeys.Keys ?? Enumerable.Empty<string>())
            warnings.Add($"Secret '{key}' could not be decrypted on this machine (was the settings file copied " +
                         "from another server?). Re-enter it in the configuration console.");
        if (layered?.LastReloadError is { } reloadError)
            warnings.Add($"The last configuration change could not be loaded and was ignored: {reloadError}");

        return new Report(errors, warnings);
    }

    private static void ValidateAlerting(AlertingOptions alerting, List<string> errors)
    {
        if (alerting.RenotifyMinutes < 0)
            errors.Add($"Alerting:RenotifyMinutes must be >= 0 (was {alerting.RenotifyMinutes}).");
        if (alerting.ResolveAfterSeconds < 0)
            errors.Add($"Alerting:ResolveAfterSeconds must be >= 0 (was {alerting.ResolveAfterSeconds}).");
    }

    /// <summary>Channel checks. Messages name the key only: URLs and secrets are credentials.</summary>
    private static void ValidateNotifications(NotificationOptions notifications, List<string> errors, List<string> warnings)
    {
        void Severity(string key, string? value)
        {
            if (!NotificationChannelResolver.TryParseSeverity(value, out _))
                errors.Add($"{key} must be Info, Warning or Critical.");
        }

        void Url(string key, string? value, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                    errors.Add($"{key} is required.");
                return;
            }
            if (!NotificationChannelResolver.IsValidUrl(value))
                errors.Add($"{key} is not a valid http(s) URL.");
            else if (new Uri(value.Trim()).Scheme == Uri.UriSchemeHttp)
                warnings.Add($"{key} uses plain HTTP: alert details travel unencrypted.");
        }

        Severity("Notifications:Email:MinSeverity", notifications.Email.MinSeverity);
        foreach (var (name, settings) in new[]
                 {
                     ("Teams", notifications.Teams), ("Slack", notifications.Slack), ("Webhook", notifications.Webhook),
                 })
        {
            Url($"Notifications:{name}:Url", settings.Url, required: false);
            Severity($"Notifications:{name}:MinSeverity", settings.MinSeverity);
        }

        if (notifications.TimeoutSeconds is < 1 or > 120)
            errors.Add($"Notifications:TimeoutSeconds must be between 1 and 120 (was {notifications.TimeoutSeconds}).");
        if (notifications.CriticalProbeCooldownSeconds is < 60 or > 3600)
        {
            errors.Add("Notifications:CriticalProbeCooldownSeconds must be between 60 and 3600 " +
                       $"(was {notifications.CriticalProbeCooldownSeconds}).");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < notifications.Channels.Count; i++)
        {
            var channel = notifications.Channels[i];
            var prefix = $"Notifications:Channels:{i}";
            if (!NotificationChannelResolver.TryParseType(channel.Type, out var type))
            {
                errors.Add($"{prefix}:Type must be Email, Webhook, Teams or Slack.");
                continue;
            }
            Severity($"{prefix}:MinSeverity", channel.MinSeverity);
            if (type != NotificationChannelType.Email && channel.Enabled)
                Url($"{prefix}:Url", channel.Url, required: true);
            if (!string.IsNullOrWhiteSpace(channel.Name) && !names.Add(channel.Name.Trim()))
                errors.Add($"{prefix}:Name '{channel.Name}' is used by more than one channel.");
        }
    }
}