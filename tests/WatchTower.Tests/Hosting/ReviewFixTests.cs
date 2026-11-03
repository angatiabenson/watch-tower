using System.Security.AccessControl;
using System.Security.Principal;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using WatchTower.Cli;
using WatchTower.Configuration;
using WatchTower.Configuration.Secrets;
using WatchTower.Configuration.Settings;
using WatchTower.Hosting;

namespace WatchTower.Tests.Hosting;

public class LegacyMigrationSafetyTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();

    public LegacyMigrationSafetyTests() => _dir.Paths.EnsureDirectories();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void LegacySettings_AreMergedBeneathAnExistingSeed()
    {
        // `install --config seed.json` writes the operator file before the first start.
        File.WriteAllText(_dir.Paths.SettingsFilePath, """{ "VisibilityPortal": { "AgentId": "from-seed" } }""");
        _dir.WriteInstallFile("watchtower.db", "db");
        _dir.WriteInstallFile("appsettings.json", """
            { "Email": { "SmtpHost": "smtp.corp" }, "VisibilityPortal": { "AgentId": "legacy", "Protocol": "Grpc" } }
            """);

        LegacyStateMigrator.Run(_dir.Paths).Error.Should().BeNull();

        var doc = new JsonSettingsStore(_dir.Paths.SettingsFilePath).Read();
        doc["Email"]!["SmtpHost"]!.GetValue<string>().Should().Be("smtp.corp");             // legacy kept
        doc["VisibilityPortal"]!["Protocol"]!.GetValue<string>().Should().Be("Grpc");        // legacy kept
        doc["VisibilityPortal"]!["AgentId"]!.GetValue<string>().Should().Be("from-seed");    // seed wins
    }

    [Fact]
    public void LegacySecrets_AreEncryptedDuringMigration()
    {
        _dir.WriteInstallFile("watchtower.db", "db");
        _dir.WriteInstallFile("appsettings.json", """{ "Email": { "Password": "legacy-plaintext" } }""");

        LegacyStateMigrator.Run(_dir.Paths);

        File.ReadAllText(_dir.Paths.SettingsFilePath).Should().NotContain("legacy-plaintext").And.Contain("dpapi:");
        File.Exists(_dir.Paths.SettingsFilePath + ".bak").Should().BeFalse();
    }

    [Fact]
    public void FailedImport_LeavesNoMarker_AndNoPartialDatabase()
    {
        _dir.WriteInstallFile("watchtower.db", "db");
        // Make the destination path unusable: a directory where the DB file should go.
        Directory.CreateDirectory(_dir.Paths.DatabasePath + ".import");

        var result = LegacyStateMigrator.Run(_dir.Paths);

        result.Error.Should().NotBeNull();
        File.Exists(_dir.Paths.LegacyMigrationMarkerPath).Should().BeFalse();
        File.Exists(_dir.Paths.DatabasePath).Should().BeFalse();
    }

    [Fact]
    public async Task PlaintextSecrets_AreEncryptedInPlace_AndBackupRemoved()
    {
        File.WriteAllText(_dir.Paths.SettingsFilePath, """{ "VisibilityPortal": { "AgentToken": "hand-edited" } }""");
        File.WriteAllText(_dir.Paths.SettingsFilePath + ".bak", "plaintext leftovers hand-edited");
        var store = new JsonSettingsStore(_dir.Paths.SettingsFilePath);

        (await store.ProtectPlaintextSecretsAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        File.ReadAllText(_dir.Paths.SettingsFilePath).Should().NotContain("hand-edited");
        File.Exists(_dir.Paths.SettingsFilePath + ".bak").Should().BeFalse();
        (await store.ProtectPlaintextSecretsAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}

public class BoundListLayeringTests
{
    [Theory]
    [InlineData("""["X"]""", new[] { "X" })]
    [InlineData("[]", new string[0])]
    public void PortalList_ReplacesOperatorList_InBoundOptions(string portalList, string[] expected)
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath,
            """{ "Monitoring": { "Services": { "WatchedServices": ["A", "B", "C"] } }, "Email": { "ToAddresses": ["a@x.io", "b@x.io"] } }""");
        File.WriteAllText(dir.Paths.RemoteConfigFilePath,
            $$"""{ "Monitoring": { "Services": { "WatchedServices": {{portalList}} } }, "Email": { "ToAddresses": ["c@x.io"] } }""");

        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, dir.Paths, "Production", reloadOnChange: false);
        var config = builder.Build();

        var services = config.GetSection(ServiceMonitorOptions.SectionName).Get<ServiceMonitorOptions>() ?? new();
        var email = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>()!;

        services.WatchedServices.Should().Equal(expected);
        email.ToAddresses.Should().Equal("c@x.io");
    }
}

public class CliInstanceSafetyTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\WatchTower\\WatchTower.exe\" --data-dir \"D:\\WT Data\"", "D:\\WT Data")]
    [InlineData("C:\\wt\\WatchTower.exe --data-dir C:\\data --service-name X", "C:\\data")]
    [InlineData("\"C:\\wt\\WatchTower.exe\" --data-dir=E:\\wt", "E:\\wt")]
    [InlineData("\"C:\\wt\\WatchTower.exe\"", null)]
    public void DataDirectory_IsParsedFromServiceCommandLine(string commandLine, string? expected)
    {
        ServiceInstaller.DataDirectoryOf(commandLine).Should().Be(expected);
    }

    [Fact]
    public void Purge_RefusesUnsafeTargets()
    {
        static string Reason(string dir) => WatchTowerCli.UnsafePurgeReason(
            new WatchTowerPaths(AppContext.BaseDirectory, dir), "WatchTower-test-nonexistent")!;

        Reason("C:\\").Should().Contain("drive root");
        Reason(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Should().Contain("system folder");
        Reason(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Should().Contain("system folder");

        using var stranger = new TestDataDirectory(); // exists, but has no WatchTower layout
        Directory.CreateDirectory(stranger.Paths.DataDirectory);
        Reason(stranger.Paths.DataDirectory).Should().Contain("does not look like");
    }

    [Fact]
    public void Purge_AllowsARealDataDirectory()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();

        WatchTowerCli.UnsafePurgeReason(dir.Paths, "WatchTower-test-nonexistent").Should().BeNull();
    }

    [Fact]
    public void ImportSettings_RejectsAnInvalidResultingConfiguration_WithoutWriting()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var seed = Path.Combine(dir.Root, "bad.json");
        File.WriteAllText(seed, """{ "Monitoring": { "Cpu": { "PollIntervalSeconds": 0 } } }""");

        var act = () => WatchTowerCli.ImportSettings(dir.Paths, new FileInfo(seed), TextWriter.Null);

        act.Should().Throw<Exception>().WithMessage("*PollIntervalSeconds*");
        File.Exists(dir.Paths.SettingsFilePath).Should().BeFalse();
    }

    [Fact]
    public void ConsoleUrl_DefaultsToLoopback_AndIsNullWhenDisabled()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();

        WatchTowerCli.ConsoleUrl(dir.Paths).Should().Be("http://127.0.0.1:8750/");

        File.WriteAllText(dir.Paths.SettingsFilePath, """{ "Ui": { "Enabled": false } }""");
        WatchTowerCli.ConsoleUrl(dir.Paths).Should().BeNull();
    }
}

public class DataDirectoryAclTests
{
    [Fact]
    public void Harden_LeavesOnlySystemAndAdministrators_WithoutInheritance()
    {
        Assert.SkipUnless(DataDirectorySecurity.IsPrivileged(), "Requires an elevated test run.");

        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.SettingsFilePath, "{}");

        DataDirectorySecurity.Harden(dir.Paths.DataDirectory);

        var acl = new DirectoryInfo(dir.Paths.DataDirectory).GetAccessControl();
        acl.AreAccessRulesProtected.Should().BeTrue();
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Distinct().ToList();
        sids.Should().BeEquivalentTo(["S-1-5-18", "S-1-5-32-544"]);

        // Children (the settings file) inherit the restricted ACL.
        var fileSids = new FileInfo(dir.Paths.SettingsFilePath).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Distinct().ToList();
        fileSids.Should().BeEquivalentTo(["S-1-5-18", "S-1-5-32-544"]);
    }
}