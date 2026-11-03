using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Configuration.Secrets;

namespace WatchTower.Hosting;

/// <summary>
/// Boot-time configuration checks. Fatal errors stop the service (fail fast instead of a
/// "zombie" that busy-loops or error-spins); everything else is logged as a warning.
/// </summary>
public static class StartupValidator
{
    public sealed record Report(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool IsValid => Errors.Count == 0;
    }

    public static Report Validate(IServiceProvider services)
    {
        T Opt<T>() where T : class => services.GetRequiredService<IOptionsMonitor<T>>().CurrentValue;

        var errors = new List<string>();
        var warnings = new List<string>();

        void RequirePositive(string name, int value)
        {
            if (value < 1) errors.Add($"{name} must be >= 1 (was {value}).");
        }

        var services_ = Opt<ServiceMonitorOptions>();
        var pools = Opt<ApplicationPoolMonitorOptions>();
        var heartbeat = Opt<HeartbeatOptions>();
        var email = Opt<EmailOptions>();
        var portal = Opt<PortalOptions>();

        RequirePositive("Monitoring:Cpu:PollIntervalSeconds", Opt<CpuMonitorOptions>().PollIntervalSeconds);
        RequirePositive("Monitoring:Ram:PollIntervalSeconds", Opt<RamMonitorOptions>().PollIntervalSeconds);
        RequirePositive("Monitoring:Disk:PollIntervalSeconds", Opt<DiskMonitorOptions>().PollIntervalSeconds);
        RequirePositive("Monitoring:Network:PollIntervalSeconds", Opt<NetworkMonitorOptions>().PollIntervalSeconds);
        RequirePositive("Monitoring:Services:PollIntervalSeconds", services_.PollIntervalSeconds);
        RequirePositive("Monitoring:ApplicationPools:PollIntervalSeconds", pools.PollIntervalSeconds);
        RequirePositive("Heartbeat:IntervalSeconds", heartbeat.IntervalSeconds);

        if (portal.Enabled)
        {
            if (string.IsNullOrWhiteSpace(portal.AgentId))
                errors.Add("VisibilityPortal.AgentId is required when the portal is enabled.");
            if (string.IsNullOrWhiteSpace(portal.AgentToken))
                errors.Add("VisibilityPortal.AgentToken is required when the portal is enabled.");
            if (string.IsNullOrWhiteSpace(portal.BaseUrl))
                errors.Add("VisibilityPortal.BaseUrl is required when the portal is enabled.");
            else if (!Uri.TryCreate(portal.BaseUrl, UriKind.Absolute, out _))
                errors.Add($"VisibilityPortal.BaseUrl '{portal.BaseUrl}' is not an absolute URL.");
            RequirePositive("VisibilityPortal:Outbox:FlushIntervalSeconds", portal.Outbox.FlushIntervalSeconds);
            RequirePositive("VisibilityPortal:CommandPoll:PollIntervalSeconds", portal.CommandPoll.PollIntervalSeconds);
            RequirePositive("VisibilityPortal:RemoteConfig:PollIntervalSeconds", portal.RemoteConfig.PollIntervalSeconds);
        }

        if (string.IsNullOrWhiteSpace(email.SmtpHost))
            warnings.Add("SMTP host is not configured. Email notifications will be disabled.");
        if (string.IsNullOrWhiteSpace(heartbeat.Url))
            warnings.Add("Heartbeat URL is not configured. Dead man's switch is disabled.");
        if (services_.WatchedServices.Count == 0)
            warnings.Add("No watched services configured. Service monitoring will be idle.");
        if (pools.WatchedApplicationPools.Count == 0)
            warnings.Add("No watched IIS application pools configured. Application pool monitoring will be idle.");

        foreach (var key in ProtectedJsonConfigurationProvider.FindUndecryptableKeys(
                     services.GetRequiredService<IConfiguration>()))
            warnings.Add($"Secret '{key}' could not be decrypted on this machine (was the settings file copied " +
                         "from another server?). Re-enter it in the configuration UI.");

        return new Report(errors, warnings);
    }
}