using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using WatchTower.Configuration.Layered;
using WatchTower.Configuration.Secrets;
using WatchTower.Hosting;

namespace WatchTower.Tests.Hosting;

public class ConfigurationLayeringTests
{
    [Fact]
    public void Layers_ApplyInOrder_DefaultsThenOperatorThenPortal()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        dir.WriteInstallFile("appsettings.json", """
            { "Monitoring": { "Cpu": { "ThresholdPercent": 99, "PollIntervalSeconds": 5, "SustainedSeconds": 300 } } }
            """);
        File.WriteAllText(dir.Paths.SettingsFilePath, """
            { "Monitoring": { "Cpu": { "ThresholdPercent": 90, "PollIntervalSeconds": 10 } } }
            """);
        File.WriteAllText(dir.Paths.RemoteConfigFilePath, """
            { "Monitoring": { "Cpu": { "ThresholdPercent": 80 } } }
            """);

        var config = Build(dir.Paths);

        config["Monitoring:Cpu:ThresholdPercent"].Should().Be("80");    // portal wins
        config["Monitoring:Cpu:PollIntervalSeconds"].Should().Be("10"); // operator beats default
        config["Monitoring:Cpu:SustainedSeconds"].Should().Be("300");   // default survives
    }

    [Fact]
    public void OperatorSettings_ProtectedSecretsAreDecryptedTransparently()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var cipher = DpapiSecretProtector.Instance.Protect("s3cret!");
        File.WriteAllText(dir.Paths.SettingsFilePath, $$"""{ "Email": { "Password": "{{cipher}}" } }""");

        File.ReadAllText(dir.Paths.SettingsFilePath).Should().NotContain("s3cret!");
        Build(dir.Paths)["Email:Password"].Should().Be("s3cret!");
    }

    [Fact]
    public void OperatorSettings_UndecryptableSecret_BecomesEmptyAndIsReported()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, """{ "Email": { "Password": "dpapi:bm90LWEtcmVhbC1ibG9i" } }""");

        var config = Build(dir.Paths);

        config["Email:Password"].Should().BeEmpty();
        LayeredJsonConfigurationProvider.Find(config)!.UndecryptableKeys.Should().ContainKey("Email:Password");
    }

    [Fact]
    public void MissingOptionalFiles_DoNotFail()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();

        var act = () => Build(dir.Paths);

        act.Should().NotThrow();
    }

    [Fact]
    public void HigherLayerList_ReplacesLowerListEntirely()
    {
        // Regression: index-wise merging turned ["A","B","C"] + ["X"] into ["X","B","C"].
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, """
            { "Monitoring": { "Services": { "WatchedServices": ["A", "B", "C"] } } }
            """);
        File.WriteAllText(dir.Paths.RemoteConfigFilePath, """
            { "Monitoring": { "Services": { "WatchedServices": ["X"] } } }
            """);

        var config = Build(dir.Paths);

        config.GetSection("Monitoring:Services:WatchedServices").Get<string[]>().Should().Equal("X");
    }

    [Fact]
    public void Origins_ReportWhichLayerSuppliedEachKey()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        dir.WriteInstallFile("appsettings.json", """{ "Email": { "SmtpPort": 587, "FromName": "WatchTower" } }""");
        File.WriteAllText(dir.Paths.SettingsFilePath,
            """{ "Email": { "SmtpPort": 25 }, "Monitoring": { "Services": { "WatchedServices": ["A"] } } }""");
        File.WriteAllText(dir.Paths.RemoteConfigFilePath,
            """{ "Monitoring": { "Services": { "WatchedServices": ["X"] } } }""");

        var layered = LayeredJsonConfigurationProvider.Find(Build(dir.Paths))!;

        layered.GetOrigin("Email:FromName").Should().Be(ConfigLayer.Default);
        layered.GetOrigin("Email:SmtpPort").Should().Be(ConfigLayer.Operator);
        layered.GetOrigin("Monitoring:Services:WatchedServices").Should().Be(ConfigLayer.Portal);
        layered.GetOrigin("Email:Password").Should().BeNull();
    }

    [Fact]
    public async Task OperatorFileChange_IsPickedUpByReload()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, """{ "Display": { "Timezone": "UTC" } }""");
        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, dir.Paths, "Production", reloadOnChange: true);
        var config = builder.Build();
        var reloaded = new TaskCompletionSource();
        Microsoft.Extensions.Primitives.ChangeToken.OnChange(config.GetReloadToken, () => reloaded.TrySetResult());

        File.WriteAllText(dir.Paths.SettingsFilePath, """{ "Display": { "Timezone": "Africa/Nairobi" } }""");
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        config["Display:Timezone"].Should().Be("Africa/Nairobi");
        (config as IDisposable)?.Dispose();
    }

    [Fact]
    public void MalformedOperatorFile_FailsWithFilePathInMessage()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, "{ not json");

        var act = () => Build(dir.Paths);

        act.Should().Throw<InvalidDataException>().WithMessage("*watchtower.settings.json*");
    }

    private static IConfigurationRoot Build(WatchTowerPaths paths)
    {
        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, paths, "Production", reloadOnChange: false);
        return builder.Build();
    }
}