using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Configuration.Layered;
using WatchTower.Hosting;

namespace WatchTower.Portal.Actions;

/// <summary>Which services / application pools a portal command may act on.</summary>
public interface ICommandTargetPolicy
{
    IReadOnlyList<string> AllowedServices();
    IReadOnlyList<string> AllowedApplicationPools();

    /// <summary>A reason to refuse acting on <paramref name="serviceName"/> regardless of the allow-list, or null.</summary>
    string? DenyService(string serviceName);
}

/// <summary>
/// The allow-list comes from LOCAL configuration only — shipped defaults, the operator's
/// settings and environment variables — never from the portal's remote-config overlay.
/// Otherwise a compromised portal could first push <c>WatchedServices: ["WinDefend"]</c> and
/// then stop it, as LocalSystem. Operators who want the portal to manage the list as well can
/// opt in with <c>VisibilityPortal:CommandPoll:AllowPortalManagedTargets</c>.
/// The agent's own service is never a valid target (use the AgentRestart action instead).
/// </summary>
public sealed class LocalCommandTargetPolicy(
    IConfiguration configuration,
    IOptionsMonitor<ServiceMonitorOptions> services,
    IOptionsMonitor<ApplicationPoolMonitorOptions> pools,
    IOptionsMonitor<PortalOptions> portal,
    InstanceIdentity instance) : ICommandTargetPolicy
{
    public IReadOnlyList<string> AllowedServices() =>
        Resolve($"{ServiceMonitorOptions.SectionName}:{nameof(ServiceMonitorOptions.WatchedServices)}",
            services.CurrentValue.WatchedServices);

    public IReadOnlyList<string> AllowedApplicationPools() =>
        Resolve($"{ApplicationPoolMonitorOptions.SectionName}:{nameof(ApplicationPoolMonitorOptions.WatchedApplicationPools)}",
            pools.CurrentValue.WatchedApplicationPools);

    public string? DenyService(string serviceName) =>
        string.Equals(serviceName.Trim(), instance.ServiceName, StringComparison.OrdinalIgnoreCase)
            ? "WatchTower does not stop or restart its own service through service commands; use AgentRestart"
            : null;

    private IReadOnlyList<string> Resolve(string key, IReadOnlyList<string> effective)
    {
        if (portal.CurrentValue.CommandPoll.AllowPortalManagedTargets)
            return effective;

        var layered = LayeredJsonConfigurationProvider.Find(configuration);
        if (layered is null)
            return effective; // no layered configuration (tests / hosts without the portal overlay)

        // Built ONLY from local sources: the portal-free JSON layers plus entries that
        // environment variables set. Never trust the effective list wholesale — env entries are
        // merged by index with lower layers, so the effective list can still hold portal entries
        // at indexes the environment did not set.
        var allowed = new List<string>(layered.GetLocalList(key));
        var root = configuration as IConfigurationRoot;
        foreach (var env in root?.Providers.OfType<EnvironmentVariablesConfigurationProvider>() ?? [])
        {
            foreach (var index in env.GetChildKeys([], key).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (env.TryGet($"{key}:{index}", out var value) && !string.IsNullOrWhiteSpace(value) &&
                    !allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                    allowed.Add(value.Trim());
            }
        }
        return allowed;
    }
}