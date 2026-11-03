using AwesomeAssertions;

using WatchTower.Hosting;

namespace WatchTower.Tests.Hosting;

public class LegacyStateMigratorTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();

    public LegacyStateMigratorTests() => _dir.Paths.EnsureDirectories();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void LegacyInstall_CopiesStateAndCustomizedSettings_LeavingOriginals()
    {
        _dir.WriteInstallFile("watchtower.db", "db");
        _dir.WriteInstallFile("watchtower.db-wal", "wal");
        _dir.WriteInstallFile("remote-config.json", "{}");
        _dir.WriteInstallFile("last-report.txt", "2026-01-01");
        _dir.WriteInstallFile("appsettings.json", """{ "Email": { "SmtpHost": "smtp.corp" } }""");

        var result = LegacyStateMigrator.Run(_dir.Paths);

        result.Error.Should().BeNull();
        File.ReadAllText(_dir.Paths.DatabasePath).Should().Be("db");
        File.ReadAllText(_dir.Paths.DatabasePath + "-wal").Should().Be("wal");
        File.Exists(_dir.Paths.RemoteConfigFilePath).Should().BeTrue();
        File.ReadAllText(_dir.Paths.LastReportMarkerPath).Should().Be("2026-01-01");
        File.ReadAllText(_dir.Paths.SettingsFilePath).Should().Contain("smtp.corp");

        // Originals stay so a rollback to an older build still finds its state.
        File.Exists(Path.Combine(_dir.Paths.InstallDirectory, "watchtower.db")).Should().BeTrue();
    }

    [Fact]
    public void FreshInstall_DoesNotCopyShippedDefaultsAsOperatorSettings()
    {
        _dir.WriteInstallFile("appsettings.json", "{}");

        LegacyStateMigrator.Run(_dir.Paths);

        File.Exists(_dir.Paths.SettingsFilePath).Should().BeFalse();
        File.Exists(_dir.Paths.LegacyMigrationMarkerPath).Should().BeTrue();
    }

    [Fact]
    public void RunsOnlyOnce()
    {
        LegacyStateMigrator.Run(_dir.Paths);
        _dir.WriteInstallFile("watchtower.db", "late");

        var second = LegacyStateMigrator.Run(_dir.Paths);

        second.Ran.Should().BeFalse();
        File.Exists(_dir.Paths.DatabasePath).Should().BeFalse();
    }

    [Fact]
    public void ExistingDataDirectoryState_IsNeverOverwritten()
    {
        File.WriteAllText(_dir.Paths.DatabasePath, "current");
        File.WriteAllText(_dir.Paths.SettingsFilePath, "{ \"x\": 1 }");
        _dir.WriteInstallFile("watchtower.db", "legacy");
        _dir.WriteInstallFile("appsettings.json", "{ \"x\": 2 }");

        LegacyStateMigrator.Run(_dir.Paths);

        File.ReadAllText(_dir.Paths.DatabasePath).Should().Be("current");
        File.ReadAllText(_dir.Paths.SettingsFilePath).Should().Contain("1");
    }
}