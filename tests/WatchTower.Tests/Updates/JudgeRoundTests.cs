using AwesomeAssertions;

using WatchTower.Cli;
using WatchTower.Hosting;
using WatchTower.Tests.Hosting;
using WatchTower.Updates;

namespace WatchTower.Tests.Updates;

/// <summary>Regression tests for the M2–M4 judge findings.</summary>
public class UpdateCompatibilityTests
{
    [Fact]
    public void NewerManifestSchema_IsBlockedWithAReason()
    {
        var reason = UpdateService.BlockedReason(new UpdateManifest
        {
            Version = "99.0.0",
            Schema = UpdateService.SupportedManifestSchema + 1,
            Assets = [],
        });

        reason.Should().Contain("newer release format");
    }

    [Fact]
    public void MinimumVersionAboveTheRunningVersion_IsBlockedWithAReason()
    {
        var reason = UpdateService.BlockedReason(new UpdateManifest
        {
            Version = "99.0.0",
            MinimumVersion = "98.0.0",
            Assets = [],
        });

        reason.Should().Contain("98.0.0").And.Contain("Upgrade to");
    }

    [Theory]
    [InlineData(0, null, "invalid")]          // judge probe: schema 0 was accepted
    [InlineData(-1, null, "invalid")]         // judge probe: schema -1 was accepted
    [InlineData(1, "banana", "invalid minimum version")] // judge probe: unparsable minimum was ignored
    public void MalformedCompatibilityMetadata_IsBlocked(int schema, string? minimumVersion, string expected)
    {
        UpdateService.BlockedReason(new UpdateManifest
        {
            Version = "99.0.0",
            Schema = schema,
            MinimumVersion = minimumVersion,
            Assets = [],
        }).Should().Contain(expected);
    }

    [Fact]
    public void CompatibleManifest_IsNotBlocked()
    {
        UpdateService.BlockedReason(new UpdateManifest { Version = "99.0.0", MinimumVersion = "0.0.0-a", Assets = [] })
            .Should().BeNull();
    }
}

public class PackageFileListTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Upgrade_RemovesFilesOnlyThePreviousPackageShipped_AndKeepsEverythingElse()
    {
        var target = Path.Combine(_dir.Root, "app");
        var v1 = Path.Combine(_dir.Root, "v1");
        var v2 = Path.Combine(_dir.Root, "v2");
        Directory.CreateDirectory(v1);
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v1, "WatchTower.exe"), "1");
        File.WriteAllText(Path.Combine(v1, "old-only.dll"), "1");
        File.WriteAllText(Path.Combine(v2, "WatchTower.exe"), "2");

        UpdateApplier.InstallPackage(v1, target, retries: 0);
        File.WriteAllText(Path.Combine(target, "operator-notes.txt"), "keep me"); // never shipped
        UpdateApplier.InstallPackage(v2, target, retries: 0);

        File.ReadAllText(Path.Combine(target, "WatchTower.exe")).Should().Be("2");
        File.Exists(Path.Combine(target, "old-only.dll")).Should().BeFalse();
        File.Exists(Path.Combine(target, "operator-notes.txt")).Should().BeTrue();
    }

    [Fact]
    public void CraftedFileList_CannotDeleteOutsideTheInstallFolder()
    {
        var target = Path.Combine(_dir.Root, "app");
        var outside = Path.Combine(_dir.Root, "outside.txt");
        var pkg = Path.Combine(_dir.Root, "pkg");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(pkg);
        File.WriteAllText(outside, "precious");
        File.WriteAllText(Path.Combine(pkg, "WatchTower.exe"), "2");
        File.WriteAllLines(Path.Combine(target, ".watchtower-files"), [@"..\outside.txt"]);

        UpdateApplier.InstallPackage(pkg, target, retries: 0);

        File.Exists(outside).Should().BeTrue();
    }
}

public class ImportLegacyCommandTests
{
    [Fact]
    public async Task ImportLegacy_CapturesHandEditedSettings_BeforeTheyAreOverwritten()
    {
        Assert.SkipUnless(DataDirectorySecurity.IsPrivileged(), "Requires an elevated test run.");

        using var dir = new TestDataDirectory();
        var legacy = Path.Combine(dir.Root, "legacy-install");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "watchtower.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "appsettings.json"), """{ "Email": { "SmtpHost": "smtp.legacy.corp" } }""");

        var output = new StringWriter();
        var code = await WatchTowerCli.RunAsync(
            ["import-legacy", "--from", legacy, "--data-dir", dir.Paths.DataDirectory, "--service-name", "WatchTowerTestNone"],
            output, output);

        code.Should().Be(0, output.ToString());
        File.ReadAllText(dir.Paths.SettingsFilePath).Should().Contain("smtp.legacy.corp");
        File.Exists(dir.Paths.LegacyMigrationMarkerPath).Should().BeTrue(); // the new service will not re-import defaults
    }
}