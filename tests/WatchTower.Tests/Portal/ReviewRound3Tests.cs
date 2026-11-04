using System.Net;
using System.Security.Cryptography;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.Updates;
using WatchTower.Updates;

namespace WatchTower.Tests.Portal;

/// <summary>Regression tests for review round 3.</summary>
public class AllowListEnvironmentTests
{
    [Fact]
    public void EnvironmentEntry_DoesNotMakeTheWholeEffectiveListTrusted()
    {
        // Reviewer repro: env sets index 0; the portal overlay supplies index 1 ("Admin").
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        File.WriteAllText(dir.Paths.RemoteConfigFilePath, """
            { "Monitoring": { "ApplicationPools": { "WatchedApplicationPools": ["Billing", "Admin"] } } }
            """);
        var prefix = $"WTTEST_{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable($"{prefix}Monitoring__ApplicationPools__WatchedApplicationPools__0", "Billing");
        try
        {
            var builder = new ConfigurationBuilder();
            WatchTowerHost.ConfigureSources(builder, dir.Paths, "Production", reloadOnChange: false);
            builder.AddEnvironmentVariables(prefix);
            var config = builder.Build();

            var pools = config.GetSection(ApplicationPoolMonitorOptions.SectionName).Get<ApplicationPoolMonitorOptions>()!;
            pools.WatchedApplicationPools.Should().Contain("Admin"); // the effective list does include it

            var policy = new LocalCommandTargetPolicy(config,
                Mock.Of<IOptionsMonitor<ServiceMonitorOptions>>(m => m.CurrentValue == new ServiceMonitorOptions()),
                Mock.Of<IOptionsMonitor<ApplicationPoolMonitorOptions>>(m => m.CurrentValue == pools),
                Mock.Of<IOptionsMonitor<PortalOptions>>(m => m.CurrentValue == new PortalOptions()),
                new InstanceIdentity("WatchTower"));

            policy.AllowedApplicationPools().Should().Equal("Billing");
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{prefix}Monitoring__ApplicationPools__WatchedApplicationPools__0", null);
        }
    }
}

public class CommandRetentionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_ret_{Guid.NewGuid()}.db");
    private readonly SqliteCommandStore _store;

    public CommandRetentionTests() => _store = new SqliteCommandStore(_dbPath);

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task UndeliveredResults_SurviveRetention_UntilThePortalAcceptsThem()
    {
        var command = new AgentCommandDto("c1", (int)ActionType.ForceMetricSnapshot, [], null,
            DateTime.UtcNow.AddDays(-90).ToString("O"));
        await _store.SaveCommandAsync(command);
        await _store.TryBeginExecutionAsync("c1");
        await _store.MarkCompletedAsync("c1", new CommandResultDto("c1", true, "ok", DateTime.UtcNow.AddDays(-60).ToString("O")));

        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow)).Should().Be(0);
        (await _store.GetUnsentResultsAsync()).Should().ContainSingle();

        await _store.MarkResultSentAsync("c1");
        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow)).Should().Be(1);
    }
}

public class UpdateInterleavingTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
    private readonly UpdateOptions _options = new() { FeedUrl = "https://updates.example.com/m.json" };

    public UpdateInterleavingTests() => _dir.Paths.EnsureDirectories();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task CheckDuringDownload_CannotSwapTheReleaseBeingStaged()
    {
        var ct = TestContext.Current.CancellationToken;
        var package = UpdateServiceTests.BuildZip(("WatchTower.exe", "A"));
        var http = new GatedHttp(package);
        http.Manifest = UpdateFeedClientTests.Manifest("9.0.0", Convert.ToHexStringLower(SHA256.HashData(package)));
        var service = new UpdateService(
            new UpdateFeedClient(http.Factory(), NullLogger<UpdateFeedClient>.Instance), http.Factory(),
            Mock.Of<IOptionsMonitor<UpdateOptions>>(m => m.CurrentValue == _options), _dir.Paths,
            new AgentInfo(_dir.Paths, _time) { IsWindowsService = true }, new InstanceIdentity("WatchTowerTest"),
            _time, NullLogger<UpdateService>.Instance)
        { PackageValidator = (_, _) => { } };

        await service.CheckAsync(ct);
        service.TryStartDownload(countdown: false, out _).Should().BeTrue();
        await http.PackageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Mid-download a newer release appears; the check must not replace the offered release.
        http.Manifest = UpdateFeedClientTests.Manifest("9.1.0", "00");
        (await service.CheckAsync(ct)).Available!.Version.Should().Be("9.0.0");
        service.TryInstallNow(out _).Should().BeFalse();

        http.ReleasePackage.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.GetStatus().Phase != UpdatePhase.ReadyToInstall && DateTime.UtcNow < deadline)
            await Task.Delay(25, ct);

        var status = service.GetStatus();
        status.Phase.Should().Be(UpdatePhase.ReadyToInstall);
        status.Available!.Version.Should().Be("9.0.0"); // the staged package and the advertised release agree
        Directory.Exists(Path.Combine(service.StagingRoot, "9.0.0")).Should().BeTrue();
    }

    /// <summary>Serves the manifest immediately and holds the package download until released.</summary>
    private sealed class GatedHttp(byte[] package) : HttpMessageHandler
    {
        public string Manifest { get; set; } = "";
        public TaskCompletionSource PackageRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePackage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IHttpClientFactory Factory() => new ClientFactory(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith(".zip", StringComparison.Ordinal))
            {
                PackageRequested.TrySetResult();
                await ReleasePackage.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Manifest, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }
}