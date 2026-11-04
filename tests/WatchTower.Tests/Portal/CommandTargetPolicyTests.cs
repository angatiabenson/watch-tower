using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Portal.Actions;
using WatchTower.Tests.Hosting;

namespace WatchTower.Tests.Portal;

public class CommandTargetPolicyTests
{
    [Fact]
    public void PortalPushedWatchList_DoesNotWidenWhatCommandsMayTarget()
    {
        using var dir = Setup(allowPortalManaged: false, out var policy);

        // Effective config watches the portal's list, but commands are limited to the local one.
        policy.AllowedServices().Should().Equal("W3SVC");
        policy.AllowedApplicationPools().Should().Equal("Billing");
    }

    [Fact]
    public void OptIn_LetsThePortalManageTargets()
    {
        using var dir = Setup(allowPortalManaged: true, out var policy);

        policy.AllowedServices().Should().Equal("WinDefend", "W3SVC");
    }

    [Fact]
    public void OwnService_IsNeverATarget()
    {
        using var dir = Setup(allowPortalManaged: true, out var policy);

        policy.DenyService("watchtower").Should().Contain("AgentRestart");
        policy.DenyService("W3SVC").Should().BeNull();
    }

    private static TestDataDirectory Setup(bool allowPortalManaged, out ICommandTargetPolicy policy)
    {
        var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, """
            { "Monitoring": { "Services": { "WatchedServices": ["W3SVC"] },
                              "ApplicationPools": { "WatchedApplicationPools": ["Billing"] } } }
            """);
        File.WriteAllText(dir.Paths.RemoteConfigFilePath, """
            { "Monitoring": { "Services": { "WatchedServices": ["WinDefend", "W3SVC"] },
                              "ApplicationPools": { "WatchedApplicationPools": ["Billing", "Admin"] } } }
            """);

        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, dir.Paths, "Production", reloadOnChange: false);
        var config = builder.Build();

        IOptionsMonitor<T> Monitor<T>(string section) where T : class, new() =>
            Mock.Of<IOptionsMonitor<T>>(m => m.CurrentValue == (config.GetSection(section).Get<T>() ?? new T()));

        var portal = new PortalOptions();
        portal.CommandPoll.AllowPortalManagedTargets = allowPortalManaged;

        policy = new LocalCommandTargetPolicy(config,
            Monitor<ServiceMonitorOptions>(ServiceMonitorOptions.SectionName),
            Monitor<ApplicationPoolMonitorOptions>(ApplicationPoolMonitorOptions.SectionName),
            Mock.Of<IOptionsMonitor<PortalOptions>>(m => m.CurrentValue == portal),
            new InstanceIdentity("WatchTower"));
        return dir;
    }
}