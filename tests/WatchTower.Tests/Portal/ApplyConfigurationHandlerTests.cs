using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Tests.Alerting;
using WatchTower.Tests.Hosting;

namespace WatchTower.Tests.Portal;

/// <summary>
/// "Allow the portal to manage settings" (VisibilityPortal:RemoteConfig:Enabled) must cover the
/// ApplyConfiguration command (action 401) as well as config polling.
/// </summary>
public class ApplyConfigurationHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string ConfigJson = JsonSerializer.Serialize(
        new { services = new { watchedServices = new[] { "W3SVC" } } });

    private static (ApplyConfigurationHandler Handler, Mock<IRemoteConfigStore> Store) Create(TestDataDirectory dir, bool remoteConfigEnabled)
    {
        var store = new Mock<IRemoteConfigStore>();
        var portal = new PortalOptions { Enabled = true, RemoteConfig = { Enabled = remoteConfigEnabled } };
        var handler = new ApplyConfigurationHandler(store.Object, dir.Paths, new TestOptionsMonitor<PortalOptions>(portal),
            NullLogger<ApplyConfigurationHandler>.Instance);
        return (handler, store);
    }

    [Fact]
    public async Task PortalManagedSettingsTurnedOff_RefusesTheCommand_AndWritesNothing()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var (handler, store) = Create(dir, remoteConfigEnabled: false);

        var result = await handler.ExecuteAsync(new Dictionary<string, string> { ["ConfigJson"] = ConfigJson, ["Version"] = "v1" }, Ct);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(ApplyConfigurationHandler.DisabledMessage);
        File.Exists(dir.Paths.RemoteConfigFilePath).Should().BeFalse();
        store.Verify(s => s.SaveConfigAsync(It.IsAny<RemoteAgentConfigDto>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PortalManagedSettingsAllowed_AppliesTheCommand()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var (handler, store) = Create(dir, remoteConfigEnabled: true);

        var result = await handler.ExecuteAsync(new Dictionary<string, string> { ["ConfigJson"] = ConfigJson, ["Version"] = "v1" }, Ct);

        result.Success.Should().BeTrue(result.Message);
        File.ReadAllText(dir.Paths.RemoteConfigFilePath).Should().Contain("W3SVC");
        store.Verify(s => s.SaveConfigAsync(It.IsAny<RemoteAgentConfigDto>(), "v1"), Times.Once);
    }
}