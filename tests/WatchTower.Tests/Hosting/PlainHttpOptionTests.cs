using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using WatchTower.Hosting;
using WatchTower.Tests.Updates;
using WatchTower.Ui;
using WatchTower.Updates;

namespace WatchTower.Tests.Hosting;

/// <summary>HTTPS is the default everywhere, but operators can opt into plain HTTP per connection.</summary>
public class PlainHttpOptionTests
{
    [Fact]
    public void Console_RemoteHttp_IsAllowedWhenHttpsIsNotRequired()
    {
        var strict = UiEndpointPlanner.Plan(new UiOptions { Urls = ["http://0.0.0.0:8750"], AllowRemoteAccess = true });
        strict.Endpoints.Should().BeEmpty();

        var relaxed = UiEndpointPlanner.Plan(new UiOptions
        {
            Urls = ["http://0.0.0.0:8750"],
            AllowRemoteAccess = true,
            RequireHttpsForRemoteAccess = false,
        });
        relaxed.Problems.Should().BeEmpty();
        relaxed.Endpoints.Should().ContainSingle().Which.Https.Should().BeFalse();
    }

    [Fact]
    public void Console_RemoteHttp_StillNeedsRemoteAccessEnabled()
    {
        var plan = UiEndpointPlanner.Plan(new UiOptions
        {
            Urls = ["http://0.0.0.0:8750"],
            AllowRemoteAccess = false,
            RequireHttpsForRemoteAccess = false,
        });

        plan.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateFeed_OverHttp_IsAllowedWhenHttpsIsNotRequired()
    {
        var http = new FakeHttp().Json("http://updates.corp.local/watchtower-update.json",
            UpdateFeedClientTests.Manifest("7.0.0"));
        var client = new UpdateFeedClient(http.Factory(), NullLogger<UpdateFeedClient>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var strict = () => client.GetLatestAsync(
            new UpdateOptions { FeedUrl = "http://updates.corp.local/watchtower-update.json" }, ct);
        await strict.Should().ThrowAsync<InvalidDataException>().WithMessage("*Updates:RequireHttps*");

        var manifest = await client.GetLatestAsync(new UpdateOptions
        {
            FeedUrl = "http://updates.corp.local/watchtower-update.json",
            RequireHttps = false,
        }, ct);
        manifest!.Version.Should().Be("7.0.0");
    }

    [Theory]
    [InlineData(true, false)]  // HTTPS required + http URL => configuration error
    [InlineData(false, true)]  // HTTPS not required + http URL => valid, with a warning
    public void Portal_HttpBaseUrl_FollowsRequireTls(bool requireTls, bool expectValid)
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, $$"""
            { "VisibilityPortal": { "Enabled": true, "AgentId": "a", "AgentToken": "t",
                                    "BaseUrl": "http://portal.corp.local", "Tls": { "RequireTls": {{(requireTls ? "true" : "false")}} } } }
            """);
        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, dir.Paths, "Production", reloadOnChange: false);

        var report = StartupValidator.Validate(builder.Build());

        report.IsValid.Should().Be(expectValid);
        if (expectValid)
            report.Warnings.Should().ContainMatch("*plain HTTP*");
        else
            report.Errors.Should().ContainMatch("*Require HTTPS*");
    }
}