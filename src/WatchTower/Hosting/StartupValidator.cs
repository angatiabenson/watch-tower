using WatchTower.Configuration;
using WatchTower.Configuration.Layered;
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
                errors.Add("VisibilityPortal.BaseUrl must use https:// while Tls.RequireTls is enabled.");
            RequirePositive("VisibilityPortal:Outbox:FlushIntervalSeconds", portal.Outbox.FlushIntervalSeconds);
            RequirePositive("VisibilityPortal:CommandPoll:PollIntervalSeconds", portal.CommandPoll.PollIntervalSeconds);
            RequirePositive("VisibilityPortal:RemoteConfig:PollIntervalSeconds", portal.RemoteConfig.PollIntervalSeconds);
        }

        if (string.IsNullOrWhiteSpace(email.SmtpHost))
            warnings.Add("SMTP host is not configured. Email notifications will be disabled.");
        else if (email.ToAddresses.Count == 0)
            warnings.Add("No email recipients configured. Alerts and reports will not be sent.");
        if (string.IsNullOrWhiteSpace(heartbeat.Url))
            warnings.Add("Heartbeat URL is not configured. Dead man's switch is disabled.");
        if (watchedServices.WatchedServices.Count == 0)
            warnings.Add("No watched services configured. Service monitoring will be idle.");
        if (pools.WatchedApplicationPools.Count == 0)
            warnings.Add("No watched IIS application pools configured. Application pool monitoring will be idle.");

        // UI binding problems never stop monitoring: the console is simply not started.
        foreach (var problem in UiEndpointPlanner.Plan(Bind<UiOptions>(UiOptions.SectionName)).Problems)
            warnings.Add($"Web console: {problem}");

        var layered = LayeredJsonConfigurationProvider.Find(configuration);
        foreach (var key in layered?.UndecryptableKeys.Keys ?? Enumerable.Empty<string>())
            warnings.Add($"Secret '{key}' could not be decrypted on this machine (was the settings file copied " +
                         "from another server?). Re-enter it in the configuration console.");
        if (layered?.LastReloadError is { } reloadError)
            warnings.Add($"The last configuration change could not be loaded and was ignored: {reloadError}");

        return new Report(errors, warnings);
    }
}