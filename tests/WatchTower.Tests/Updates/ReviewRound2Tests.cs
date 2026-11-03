using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration.Secrets;
using WatchTower.Configuration.Settings;
using WatchTower.Hosting;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.Ui;
using WatchTower.Updates;

namespace WatchTower.Tests.Updates;

/// <summary>Regression tests for the second review round.</summary>
public class UpdateConcurrencyTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
    private readonly FakeHttp _http = new();
    private readonly UpdateOptions _options = new() { FeedUrl = "https://updates.example.com/m.json" };

    public UpdateConcurrencyTests() => _dir.Paths.EnsureDirectories();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhileInstalling_CheckAndDownloadCannotResurrectTheUpdate_AndASecondInstallIsRefused()
    {
        var package = UpdateServiceTests.BuildZip(("WatchTower.exe", "placeholder"));
        _http.Bytes("https://dl/pkg.zip", package)
             .Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", Convert.ToHexStringLower(SHA256.HashData(package))));
        var service = CreateService();
        await service.CheckAsync(Ct);
        service.TryStartDownload(countdown: false, out _);
        await WaitForAsync(() => service.GetStatus().Phase == UpdatePhase.ReadyToInstall);

        // A harmless real executable stands in for the staged installer.
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"),
            Path.Combine(service.StagingRoot, "9.0.0", "WatchTower.exe"), overwrite: true);

        service.TryInstallNow(out var message).Should().BeTrue(message);
        service.GetStatus().Phase.Should().Be(UpdatePhase.Installing);

        (await service.CheckAsync(Ct)).Phase.Should().Be(UpdatePhase.Installing);       // was: back to ReadyToInstall
        service.TryStartDownload(countdown: true, out _).Should().BeFalse();
        service.TryInstallNow(out _).Should().BeFalse();                                 // no second installer

        // An installer that never takes over does not block updates forever.
        _time.Advance(UpdateService.InstallerTimeout + TimeSpan.FromSeconds(1));
        var recovered = service.GetStatus();
        recovered.Phase.Should().Be(UpdatePhase.ReadyToInstall);
        recovered.Error.Should().Contain("did not complete");
    }

    private UpdateService CreateService() => new(
        new UpdateFeedClient(_http.Factory(), NullLogger<UpdateFeedClient>.Instance),
        _http.Factory(),
        Mock.Of<IOptionsMonitor<UpdateOptions>>(m => m.CurrentValue == _options),
        _dir.Paths,
        new AgentInfo(_dir.Paths, _time) { IsWindowsService = true },
        new InstanceIdentity("WatchTowerTest"),
        _time,
        NullLogger<UpdateService>.Instance)
    {
        PackageValidator = (_, _) => { },
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(25, Ct);
        }
    }
}

public class InstallerSafetyTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();

    public InstallerSafetyTests() => _dir.Paths.EnsureDirectories();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Rollback_RemovesFilesTheFailedReleaseAdded()
    {
        var backup = Path.Combine(_dir.Root, "backup");
        var target = Path.Combine(_dir.Root, "target");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.Combine(target, "extra"));
        File.WriteAllText(Path.Combine(backup, "WatchTower.exe"), "v1");
        File.WriteAllText(Path.Combine(target, "WatchTower.exe"), "v2");
        File.WriteAllText(Path.Combine(target, "appsettings.Production.json"), "{ broken }");
        File.WriteAllText(Path.Combine(target, "extra", "new.dll"), "x");

        UpdateApplier.MirrorDirectory(backup, target, retries: 0);

        File.ReadAllText(Path.Combine(target, "WatchTower.exe")).Should().Be("v1");
        File.Exists(Path.Combine(target, "appsettings.Production.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(target, "extra")).Should().BeFalse();
    }

    [Fact]
    public async Task SecondInstaller_RefusesToRun_WhileTheFirstHoldsTheLock()
    {
        using var held = new FileStream(Path.Combine(_dir.Paths.UpdatesDirectory, "install.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var services = new Mock<IServiceControl>(MockBehavior.Strict); // must not be touched

        var result = await new UpdateApplier(
            new UpdateApplier.Request("WatchTowerTest", _dir.Paths, Path.Combine(_dir.Root, "src"), _dir.Paths.InstallDirectory,
                int.MaxValue, "1.0.0", "2.0.0", TimeSpan.FromSeconds(1)),
            services.Object, TextWriter.Null, _ => Task.CompletedTask).RunAsync();

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("already running");
        File.Exists(Path.Combine(_dir.Paths.UpdatesDirectory, "last-result.json")).Should().BeFalse();
    }
}

public class SettingsBackupSafetyTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly FakeSecretProtector _protector = new();

    public SettingsBackupSafetyTests() => _dir.Paths.EnsureDirectories();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Backup_OfAPlaintextFile_IsWrittenEncrypted()
    {
        File.WriteAllText(_dir.Paths.SettingsFilePath, """{ "Email": { "Password": "plain-secret" } }""");
        var store = new JsonSettingsStore(_dir.Paths.SettingsFilePath, _protector);

        await store.UpdateAsync(doc => doc["Display"] = new JsonObject { ["Timezone"] = "UTC" },
            TestContext.Current.CancellationToken);

        File.ReadAllText(_dir.Paths.SettingsFilePath + ".bak").Should().NotContain("plain-secret").And.Contain("dpapi:");
    }

    [Fact]
    public async Task PlaintextBackup_IsRemoved_EvenWhenThePrimaryIsAlreadyEncrypted()
    {
        File.WriteAllText(_dir.Paths.SettingsFilePath, $$"""{ "Email": { "Password": "{{_protector.Protect("x")}}" } }""");
        File.WriteAllText(_dir.Paths.SettingsFilePath + ".bak", """{ "Email": { "Password": "plain-secret" } }""");
        var store = new JsonSettingsStore(_dir.Paths.SettingsFilePath, _protector);

        await store.ProtectPlaintextSecretsAsync(TestContext.Current.CancellationToken);

        File.Exists(_dir.Paths.SettingsFilePath + ".bak").Should().BeFalse();
    }

    [Fact]
    public async Task TryUpdate_ThatAborts_WritesNothing()
    {
        File.WriteAllText(_dir.Paths.SettingsFilePath, """{ "a": 1 }""");
        var store = new JsonSettingsStore(_dir.Paths.SettingsFilePath, _protector);

        var written = await store.TryUpdateAsync(doc =>
        {
            doc["a"] = 2;
            return false;
        }, TestContext.Current.CancellationToken);

        written.Should().BeFalse();
        File.ReadAllText(_dir.Paths.SettingsFilePath).Should().Contain("1");
        File.Exists(_dir.Paths.SettingsFilePath + ".bak").Should().BeFalse();
    }
}

public class ConcurrentConfigEditTests
{
    [Fact]
    public async Task ConcurrentEdits_CannotTogetherProduceAnInvalidConfiguration()
    {
        await using var host = await ConsoleTestHost.StartAsync(settingsJson:
            """{ "Monitoring": { "Disk": { "WarningThresholdPercent": 80, "CriticalThresholdPercent": 90 } } }""");
        var a = await host.SignedInClientAsync();
        var b = await host.SignedInClientAsync();
        var ct = TestContext.Current.CancellationToken;

        // Each change is valid against the original document; together they are not (85 > 82).
        var raise = a.PutAsJsonAsync("api/config",
            new { changes = new Dictionary<string, object> { ["Monitoring:Disk:WarningThresholdPercent"] = 85 } }, ct);
        var lower = b.PutAsJsonAsync("api/config",
            new { changes = new Dictionary<string, object> { ["Monitoring:Disk:CriticalThresholdPercent"] = 82 } }, ct);
        await Task.WhenAll(raise, lower);

        var disk = JsonNode.Parse(File.ReadAllText(host.Directory.Paths.SettingsFilePath))!["Monitoring"]!["Disk"]!;
        disk["WarningThresholdPercent"]!.GetValue<double>()
            .Should().BeLessThanOrEqualTo(disk["CriticalThresholdPercent"]!.GetValue<double>());
        new[] { raise.Result.IsSuccessStatusCode, lower.Result.IsSuccessStatusCode }.Should().ContainSingle(ok => ok);
    }
}

public class DescendantAclTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void Harden_ResetsChildrenWithTheirOwnProtectedAcl_AndRemovesJunctions()
    {
        Assert.SkipUnless(DataDirectorySecurity.IsPrivileged(), "Requires an elevated test run.");

        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(Path.Combine(dir.Paths.UpdatesDirectory, "staged.exe"), "x");

        // A pre-existing child with a protected DACL that lets ordinary users modify it.
        var updates = new DirectoryInfo(dir.Paths.UpdatesDirectory);
        var loose = new DirectorySecurity();
        loose.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        loose.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        loose.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        updates.SetAccessControl(loose);

        // A junction pointing outside the data directory must never be followed.
        var outside = Path.Combine(dir.Root, "outside");
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(dir.Paths.DataDirectory, "link");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"")
        { CreateNoWindow = true, UseShellExecute = false }))
            p!.WaitForExit();

        DataDirectorySecurity.Harden(dir.Paths.DataDirectory);

        foreach (var path in new[] { dir.Paths.UpdatesDirectory, Path.Combine(dir.Paths.UpdatesDirectory, "staged.exe") })
        {
            var rules = (Directory.Exists(path)
                    ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
                    : new FileInfo(path).GetAccessControl())
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            rules.Should().OnlyContain(r => r.IsInherited, path);
            rules.Select(r => r.IdentityReference.Value).Should().NotContain(Users.Value, path);
        }

        Directory.Exists(junction).Should().BeFalse();
        Directory.Exists(outside).Should().BeTrue(); // the target itself is untouched
    }
}