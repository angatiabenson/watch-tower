using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using WatchTower.Cli;
using WatchTower.Hosting;

namespace WatchTower.Tests.Hosting;

public class StartupValidatorTests
{
    [Fact]
    public void ZeroPollInterval_IsFatal()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, """{ "Monitoring": { "Cpu": { "PollIntervalSeconds": 0 } } }""");

        var report = Validate(dir.Paths);

        report.IsValid.Should().BeFalse();
        report.Errors.Should().ContainMatch("*Monitoring:Cpu:PollIntervalSeconds*");
    }

    [Fact]
    public void PortalEnabledWithoutIdentity_IsFatal()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath,
            """{ "VisibilityPortal": { "Enabled": true, "AgentId": "", "AgentToken": "", "BaseUrl": "not a url" } }""");

        var report = Validate(dir.Paths);

        report.Errors.Should().ContainMatch("*AgentId*")
            .And.ContainMatch("*AgentToken*")
            .And.ContainMatch("*not an absolute URL*");
    }

    [Fact]
    public void Defaults_AreValid_WithWarningsForUnconfiguredFeatures()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();

        var report = Validate(dir.Paths);

        report.IsValid.Should().BeTrue();
        report.Warnings.Should().ContainMatch("*SMTP*");
    }

    private static StartupValidator.Report Validate(WatchTowerPaths paths)
    {
        using var provider = WatchTowerHost.CreateBuilder(paths).Services.BuildServiceProvider();
        return StartupValidator.Validate(provider);
    }
}

public class CliTests
{
    [Fact]
    public async Task Version_PrintsProductVersion()
    {
        var (code, output, _) = await Run("version");

        code.Should().Be(0);
        output.Trim().Should().Be(AgentInfo.ProductVersion);
    }

    [Fact]
    public async Task Status_ReportsResolvedPaths()
    {
        using var dir = new TestDataDirectory();

        var (code, output, _) = await Run("status", "--data-dir", dir.Paths.DataDirectory);

        code.Should().Be(0);
        output.Should().Contain(dir.Paths.DataDirectory).And.Contain("Service:");
    }

    [Fact]
    public async Task ConfigPath_HonoursDataDirOption()
    {
        using var dir = new TestDataDirectory();

        var (code, output, _) = await Run("config", "path", "--data-dir", dir.Paths.DataDirectory);

        code.Should().Be(0);
        output.Trim().Should().Be(dir.Paths.SettingsFilePath);
    }

    [Fact]
    public async Task UnknownCommand_FailsWithNonZeroExit()
    {
        var (code, _, error) = await Run("frobnicate");

        code.Should().NotBe(0);
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void ImportSettings_MergesAndEncryptsSecrets()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var seed = Path.Combine(dir.Root, "seed.json");
        File.WriteAllText(seed, """
            // comments are allowed in seed files
            { "VisibilityPortal": { "Enabled": true, "AgentId": "srv-01", "AgentToken": "very-secret", "BaseUrl": "https://portal.example.com" }, }
            """);

        WatchTowerCli.ImportSettings(dir.Paths, new FileInfo(seed), TextWriter.Null);

        var text = File.ReadAllText(dir.Paths.SettingsFilePath);
        text.Should().Contain("srv-01").And.NotContain("very-secret").And.Contain("dpapi:");
    }

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await WatchTowerCli.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}