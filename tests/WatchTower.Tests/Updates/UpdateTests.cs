using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Hosting;
using WatchTower.Tests.Hosting;
using WatchTower.Updates;

namespace WatchTower.Tests.Updates;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("v2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    [InlineData("1.4", "1.4.0", 0)]
    public void Compare_FollowsSemVerPrecedence(string a, string b, int expected)
    {
        Math.Sign(SemanticVersion.Parse(a).CompareTo(SemanticVersion.Parse(b))).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    public void TryParse_RejectsGarbage(string text) => SemanticVersion.TryParse(text, out _).Should().BeFalse();
}

public class UpdateSignatureTests
{
    [Fact]
    public void SignedPackage_Verifies_AndTamperingIsDetected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var asset = new UpdateAsset { Rid = "win-x64", Url = "https://x/p.zip", Sha256 = new string('a', 64) };
        asset = asset with { Signature = UpdateSignature.Sign(key, "1.2.3", "win-x64", asset.Sha256) };

        UpdateSignature.Verify(publicPem, "1.2.3", asset, out _).Should().BeTrue();
        UpdateSignature.Verify(publicPem, "1.2.4", asset, out _).Should().BeFalse();                 // version swapped
        UpdateSignature.Verify(publicPem, "1.2.3", asset with { Sha256 = new string('b', 64) }, out _).Should().BeFalse();
        UpdateSignature.Verify(publicPem, "1.2.3", asset with { Signature = null }, out var error).Should().BeFalse();
        error.Should().Contain("not signed");
    }

    [Fact]
    public void SingleLinePem_FromTheSettingsUi_IsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oneLine = key.ExportSubjectPublicKeyInfoPem().Replace("\r", "").Replace("\n", "");
        var asset = new UpdateAsset { Rid = "win-x64", Url = "https://x", Sha256 = new string('c', 64) };
        asset = asset with { Signature = UpdateSignature.Sign(key, "2.0.0", "win-x64", asset.Sha256) };

        UpdateSignature.Verify(oneLine, "2.0.0", asset, out var error).Should().BeTrue(error);
    }
}

public class UpdateFeedClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GitHubFeed_PicksNewestStableReleaseThatHasAManifest()
    {
        var http = new FakeHttp()
            .Json("https://api.github.com/repos/o/r/releases?per_page=30", """
                [
                  { "tag_name": "v3.0.0-beta.1", "draft": false, "prerelease": true, "body": "beta",
                    "assets": [ { "name": "watchtower-update.json", "browser_download_url": "https://dl/beta.json" } ] },
                  { "tag_name": "v2.1.0", "draft": false, "prerelease": false, "body": "no manifest", "assets": [] },
                  { "tag_name": "v2.0.0", "draft": false, "prerelease": false, "body": "Release notes 2.0",
                    "assets": [ { "name": "watchtower-update.json", "browser_download_url": "https://dl/2.0.json" } ] },
                  { "tag_name": "v1.9.0", "draft": false, "prerelease": false, "body": "old",
                    "assets": [ { "name": "watchtower-update.json", "browser_download_url": "https://dl/1.9.json" } ] }
                ]
                """)
            .Json("https://dl/2.0.json", Manifest("2.0.0"))
            .Json("https://dl/beta.json", Manifest("3.0.0-beta.1"));
        var client = new UpdateFeedClient(http.Factory(), NullLogger<UpdateFeedClient>.Instance);

        var stable = await client.GetLatestAsync(new UpdateOptions { FeedUrl = "https://api.github.com/repos/o/r/releases" }, Ct);
        stable!.Version.Should().Be("2.0.0");
        stable.Notes.Should().Be("Release notes 2.0"); // falls back to the release body

        var pre = await client.GetLatestAsync(new UpdateOptions
        {
            FeedUrl = "https://api.github.com/repos/o/r/releases",
            Channel = "prerelease",
        }, Ct);
        pre!.Version.Should().Be("3.0.0-beta.1");
    }

    [Fact]
    public async Task ManifestFeed_IsReadDirectly()
    {
        var http = new FakeHttp().Json("https://updates.example.com/watchtower-update.json", Manifest("5.1.0"));
        var client = new UpdateFeedClient(http.Factory(), NullLogger<UpdateFeedClient>.Instance);

        var manifest = await client.GetLatestAsync(
            new UpdateOptions { FeedUrl = "https://updates.example.com/watchtower-update.json" }, Ct);

        manifest!.Version.Should().Be("5.1.0");
    }

    [Fact]
    public async Task PlainHttpFeed_IsRefused()
    {
        var client = new UpdateFeedClient(new FakeHttp().Factory(), NullLogger<UpdateFeedClient>.Instance);

        var act = () => client.GetLatestAsync(new UpdateOptions { FeedUrl = "http://updates.example.com/m.json" }, Ct);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*https*");
    }

    internal static string Manifest(string version, string sha = "00", string url = "https://dl/pkg.zip", string? sig = null) =>
        JsonSerializer.Serialize(new UpdateManifest
        {
            Version = version,
            Assets = [new UpdateAsset { Rid = UpdateService.RuntimeId, Url = url, Sha256 = sha, Signature = sig }],
        }, UpdateFeedClient.Json);
}

public class UpdateServiceTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
    private readonly FakeHttp _http = new();
    private readonly UpdateOptions _options = new() { FeedUrl = "https://updates.example.com/m.json", RestartCountdownSeconds = 30 };
    private readonly byte[] _package;

    public UpdateServiceTests()
    {
        _dir.Paths.EnsureDirectories();
        _package = BuildZip(("WatchTower.exe", "new binary"), ("appsettings.json", "{}"));
        _http.Bytes("https://dl/pkg.zip", _package);
    }

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Check_FindsNewerVersion()
    {
        _http.Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", Sha(_package)));
        var service = CreateService();

        var status = await service.CheckAsync(Ct);

        status.Phase.Should().Be(UpdatePhase.Available);
        status.Available!.Version.Should().Be("9.0.0");
        status.LastCheckedAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task Download_VerifiesChecksum_Stages_AndStartsCountdown()
    {
        _http.Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", Sha(_package)));
        var service = CreateService();
        await service.CheckAsync(Ct);

        service.TryStartDownload(countdown: true, out _).Should().BeTrue();
        var status = await WaitForAsync(service, s => s.Phase is UpdatePhase.ReadyToInstall or UpdatePhase.Failed);

        status.Phase.Should().Be(UpdatePhase.ReadyToInstall, status.Error);
        status.RestartAt.Should().Be(_time.GetUtcNow().AddSeconds(30));
        File.ReadAllText(Path.Combine(service.StagingRoot, "9.0.0", "WatchTower.exe")).Should().Be("new binary");

        service.CountdownElapsed().Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(31));
        service.CountdownElapsed().Should().BeTrue();

        service.CancelCountdown();
        service.CountdownElapsed().Should().BeFalse();
        service.GetStatus().RestartAt.Should().BeNull();
    }

    [Fact]
    public async Task Download_WithWrongChecksum_IsRejected()
    {
        _http.Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", new string('0', 64)));
        var service = CreateService();
        await service.CheckAsync(Ct);

        service.TryStartDownload(countdown: true, out _);
        var status = await WaitForAsync(service, s => s.Error is not null);

        status.Error.Should().Contain("SHA-256");
        status.Phase.Should().Be(UpdatePhase.Available);
        Directory.Exists(Path.Combine(service.StagingRoot, "9.0.0")).Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhenSigningKeyConfigured_RequiresValidSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _options.PublicKey = key.ExportSubjectPublicKeyInfoPem();
        var sha = Sha(_package);
        var forged = UpdateSignature.Sign(otherKey, "9.0.0", UpdateService.RuntimeId, sha);
        _http.Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", sha, sig: forged));
        var service = CreateService();
        await service.CheckAsync(Ct);

        service.TryStartDownload(countdown: false, out _);
        var status = await WaitForAsync(service, s => s.Error is not null || s.Phase == UpdatePhase.ReadyToInstall);

        status.Phase.Should().Be(UpdatePhase.Available);
        status.Error.Should().Contain("signature");
    }

    [Fact]
    public async Task Install_IsRefusedOutsideAWindowsService()
    {
        _http.Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", Sha(_package)));
        var service = CreateService(asWindowsService: false);
        await service.CheckAsync(Ct);
        service.TryStartDownload(countdown: false, out _);
        await WaitForAsync(service, s => s.Phase == UpdatePhase.ReadyToInstall);

        // The download task publishes ReadyToInstall a moment before releasing its operation
        // lock, so an immediate attempt may get the transient "in progress" refusal first.
        string message;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            service.TryInstallNow(out message).Should().BeFalse();
            if (!message.Contains("in progress", StringComparison.Ordinal) || DateTime.UtcNow > deadline)
                break;
            await Task.Delay(10, Ct);
        }
        message.Should().Contain("Windows service");
    }

    [Fact]
    public async Task StagedUpdate_SurvivesARestart()
    {
        _http.Json(_options.FeedUrl, UpdateFeedClientTests.Manifest("9.0.0", Sha(_package)));
        var first = CreateService();
        await first.CheckAsync(Ct);
        first.TryStartDownload(countdown: false, out _);
        await WaitForAsync(first, s => s.Phase == UpdatePhase.ReadyToInstall);

        var afterRestart = CreateService();

        afterRestart.GetStatus().Phase.Should().Be(UpdatePhase.ReadyToInstall);
        afterRestart.GetStatus().Available!.Version.Should().Be("9.0.0");
    }

    [Fact]
    public void InstallerResult_WrittenAfterStartup_IsPickedUp()
    {
        // Regression: the installer writes last-result.json only after the new service has
        // started, so reading it once at start-up never showed the outcome.
        var service = CreateService();
        service.GetStatus().LastResult.Should().BeNull();

        File.WriteAllText(Path.Combine(_dir.Paths.UpdatesDirectory, "last-result.json"), JsonSerializer.Serialize(
            new UpdateResult("1.0.0", "1.1.0", true, "Updated from 1.0.0 to 1.1.0.", DateTimeOffset.UtcNow),
            UpdateFeedClient.Json));

        service.GetStatus().LastResult!.ToVersion.Should().Be("1.1.0");
    }

    private UpdateService CreateService(bool asWindowsService = true)
    {
        var monitor = Mock.Of<IOptionsMonitor<UpdateOptions>>(m => m.CurrentValue == _options);
        var agent = new AgentInfo(_dir.Paths, _time) { IsWindowsService = asWindowsService };
        return new UpdateService(
            new UpdateFeedClient(_http.Factory(), NullLogger<UpdateFeedClient>.Instance),
            _http.Factory(), monitor, _dir.Paths, agent, new InstanceIdentity("WatchTowerTest"), _time,
            NullLogger<UpdateService>.Instance)
        {
            PackageValidator = (staged, _) =>
            {
                if (!File.Exists(Path.Combine(staged, "WatchTower.exe")))
                    throw new InvalidDataException("missing exe");
            },
        };
    }

    private static async Task<UpdateStatus> WaitForAsync(UpdateService service, Func<UpdateStatus, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = service.GetStatus();
            if (condition(status) && status.Phase != UpdatePhase.Downloading)
                return status;
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException($"Status never matched; last: {service.GetStatus()}");
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    internal static byte[] BuildZip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        return ms.ToArray();
    }
}

public class UpdateApplierTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly string _source;
    private readonly string _target;

    public UpdateApplierTests()
    {
        _dir.Paths.EnsureDirectories();
        _source = Path.Combine(_dir.Root, "staged");
        _target = _dir.Paths.InstallDirectory;
        Directory.CreateDirectory(_source);
        File.WriteAllText(Path.Combine(_source, "WatchTower.exe"), "v2");
        File.WriteAllText(Path.Combine(_target, "WatchTower.exe"), "v1");
        File.WriteAllText(Path.Combine(_target, "appsettings.json"), "old defaults");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task HealthyNewVersion_IsKept_AndResultRecorded()
    {
        var services = new FakeServiceControl(onStart: () => WriteBeacon("2.0.0"));

        var result = await CreateApplier(services).RunAsync();

        result.Success.Should().BeTrue(result.Message);
        File.ReadAllText(Path.Combine(_target, "WatchTower.exe")).Should().Be("v2");
        File.ReadAllText(Path.Combine(_dir.Paths.UpdatesDirectory, "backup", "1.0.0", "WatchTower.exe")).Should().Be("v1");
        services.Calls.Should().Equal("stop", "start");
        File.ReadAllText(Path.Combine(_dir.Paths.UpdatesDirectory, "last-result.json")).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task NewVersionThatNeverReportsHealthy_IsRolledBack()
    {
        var services = new FakeServiceControl(onStart: () => { });

        var result = await CreateApplier(services).RunAsync();

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("restored");
        File.ReadAllText(Path.Combine(_target, "WatchTower.exe")).Should().Be("v1");
        services.Calls.Should().Equal("stop", "start", "stop", "start");
    }

    [Fact]
    public async Task FailureBeforeAnyChange_RestartsTheOldVersion()
    {
        var services = new FakeServiceControl(onStart: () => { }, failStop: true);

        var result = await CreateApplier(services).RunAsync();

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("still installed");
        File.ReadAllText(Path.Combine(_target, "WatchTower.exe")).Should().Be("v1");
        services.Calls.Should().Equal("stop", "start");
    }

    private UpdateApplier CreateApplier(FakeServiceControl services) => new(
        new UpdateApplier.Request("WatchTowerTest", _dir.Paths, _source, _target,
            OldProcessId: int.MaxValue, FromVersion: "1.0.0", ToVersion: "2.0.0",
            HealthTimeout: TimeSpan.FromMilliseconds(300)),
        services, TextWriter.Null, delay: _ => Task.Delay(1));

    private void WriteBeacon(string version) =>
        File.WriteAllText(HealthBeacon.PathFor(_dir.Paths),
            JsonSerializer.Serialize(new HealthSnapshot(version, Environment.ProcessId, DateTimeOffset.UtcNow, "WatchTowerTest")));

    private sealed class FakeServiceControl(Action onStart, bool failStop = false) : IServiceControl
    {
        public List<string> Calls { get; } = [];

        public Task StopAsync(string serviceName, TimeSpan timeout)
        {
            Calls.Add("stop");
            if (failStop && Calls.Count == 1)
                throw new InvalidOperationException("stop failed");
            return Task.CompletedTask;
        }

        public Task StartAsync(string serviceName, TimeSpan timeout)
        {
            Calls.Add("start");
            onStart();
            return Task.CompletedTask;
        }

        public bool IsRunning(string serviceName) => true;
    }
}

/// <summary>Serves canned responses per absolute URL.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public FakeHttp Json(string url, string json)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return this;
    }

    public FakeHttp Bytes(string url, byte[] data)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        return this;
    }

    public IHttpClientFactory Factory() => new ClientFactory(this);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
            ? route()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}