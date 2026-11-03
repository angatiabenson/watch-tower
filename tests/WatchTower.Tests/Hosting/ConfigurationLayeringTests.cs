using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

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
        ProtectedJsonConfigurationProvider.FindUndecryptableKeys(config).Should().Contain("Email:Password");
    }

    [Fact]
    public void MissingOptionalFiles_DoNotFail()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();

        var act = () => Build(dir.Paths);

        act.Should().NotThrow();
    }

    private static IConfigurationRoot Build(WatchTowerPaths paths)
    {
        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, paths, "Production");
        return builder.Build();
    }
}