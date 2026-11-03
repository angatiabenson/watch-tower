using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using WatchTower.Hosting;
using WatchTower.Portal.Storage;

namespace WatchTower.Tests.Hosting;

/// <summary>
/// Builds the real container and activates every hosted service. Guards against the class of
/// bug where a worker depends on a service only registered in some modes (e.g. the daily
/// reporter needing the sync-state store while the portal is disabled crashed the service).
/// </summary>
public class CompositionRootTests
{
    [Fact]
    public void StandaloneMode_AllHostedServicesResolve()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, """{ "VisibilityPortal": { "Enabled": false } }""");

        using var provider = BuildValidatedProvider(dir.Paths);

        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.Should().NotBeEmpty();
        provider.GetRequiredService<IPortalSyncState>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("Https")]
    [InlineData("Grpc")]
    public void PortalMode_AllHostedServicesResolve(string protocol)
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, $$"""
            {
              "VisibilityPortal": {
                "Enabled": true, "AgentId": "agent-1", "AgentToken": "t", "Protocol": "{{protocol}}",
                "BaseUrl": "https://portal.example.com", "RemoteConfig": { "Enabled": true }
              }
            }
            """);

        using var provider = BuildValidatedProvider(dir.Paths);

        provider.GetServices<IHostedService>().Select(h => h.GetType().Name)
            .Should().Contain(["OutboxDispatchWorker", "CommandPollingWorker", "RemoteConfigWorker"]);
    }

    private static ServiceProvider BuildValidatedProvider(WatchTowerPaths paths)
    {
        var builder = WatchTowerHost.CreateBuilder(paths);
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}