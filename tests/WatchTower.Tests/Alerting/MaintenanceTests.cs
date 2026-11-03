using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Cli;
using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.Ui;
using WatchTower.Workers;

namespace WatchTower.Tests.Alerting;

public class MaintenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly Mock<WatchTower.Portal.IPortalEventPublisher> _portal = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly TestMaintenance _maintenance = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private AlertManager Alerts(AlertingOptions? options = null) =>
        TestAlerts.Create(_metrics.Object, _time, _dispatcher, options, _portal.Object, maintenance: _maintenance);

    private Incident Disk() => new(_time.GetUtcNow().UtcDateTime, IncidentType.DiskCritical, "Drive C:\\ at 95%");

    private static readonly AlertKey Key = AlertKey.Disk("C:\\");

    // ── Alert manager ──────────────────────────────────────────────────────

    [Fact]
    public async Task AlertOpenedDuringMaintenance_IsRecordedButNotifiesOnlyOnceAfterwards()
    {
        var alerts = Alerts();
        _maintenance.Active = true;

        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);

        _metrics.Verify(m => m.AddIncident(It.IsAny<Incident>()), Times.Once);
        _portal.Verify(p => p.PublishAlertEventAsync(It.IsAny<Incident>(), "disk:C:\\", false, It.IsAny<CancellationToken>()), Times.Once);
        _dispatcher.Sent.Should().BeEmpty();

        _maintenance.Active = false;
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);

        _dispatcher.Sent.Should().ContainSingle().Which.Kind.Should().Be(AlertNotificationKind.Opened);
    }

    [Fact]
    public async Task ReminderDueDuringMaintenance_IsSentOnceWhenItEnds()
    {
        var alerts = Alerts(new AlertingOptions { RenotifyMinutes = 30 });
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);

        _maintenance.Active = true;
        _time.Advance(TimeSpan.FromMinutes(45));
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);
        _dispatcher.Sent.Should().HaveCount(1);

        _maintenance.Active = false;
        _time.Advance(TimeSpan.FromMinutes(1));
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);

        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Reminder);
    }

    [Fact]
    public async Task ResolutionAndEventsDuringMaintenance_AreNotNotified()
    {
        var alerts = Alerts();
        await alerts.RaiseAsync(Key, AlertSeverity.Critical, "Disk Critical", Disk(), Ct);
        _maintenance.Active = true;

        await alerts.ResolveAsync(Key, ct: Ct);
        await alerts.NotifyEventAsync(AlertKey.Reboot, AlertSeverity.Warning, "Server restarted",
            new Incident(_time.GetUtcNow().UtcDateTime, IncidentType.ServerRestarted, "rebooted"), Ct);

        _dispatcher.Sent.Should().ContainSingle().Which.Kind.Should().Be(AlertNotificationKind.Opened);
        _portal.Verify(p => p.PublishAlertEventAsync(It.IsAny<Incident>(), "disk:C:\\", true, It.IsAny<CancellationToken>()), Times.Once);
        _metrics.Verify(m => m.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServerRestarted)), Times.Once);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    [InlineData("2026-01-01T01:00:00Z", true)]
    [InlineData("2026-01-01T01:00:00", true)] // no offset = UTC
    [InlineData("2026-01-01T03:30:00+03:00", true)]
    [InlineData("2025-12-31T23:59:59Z", false)]
    public void MaintenanceState_IsActiveUntilTheEndTime(string until, bool active)
    {
        var options = new TestOptionsMonitor<MaintenanceOptions>(new MaintenanceOptions { Until = until, Reason = " patching " });

        var window = new MaintenanceState(options, _time).Current;

        window.Active.Should().Be(active);
        window.Reason.Should().Be("patching");
    }

    // ── Auto-restart is paused ─────────────────────────────────────────────

    [Fact]
    public async Task ServiceDownDuringMaintenance_IsRecordedButNotRestarted()
    {
        var manager = new Mock<IWindowsServiceManager>();
        manager.Setup(m => m.GetServiceStatus("Svc")).Returns(
            new ServiceStatus(DateTime.UtcNow, "Svc", "Service", "Stopped", false, "Automatic"));
        manager.Setup(m => m.RestartService("Svc", It.IsAny<TimeSpan>())).Returns(true);
        var options = new Mock<IOptionsMonitor<ServiceMonitorOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            WatchedServices = ["Svc"],
            RestartDelaySeconds = 0,
            StableStateDelaySeconds = 0,
            StartupGraceSeconds = 0,
        });
        var worker = new ServiceMonitorWorker(manager.Object, _metrics.Object, Alerts(), _portal.Object, options.Object, _time,
            NullLogger<ServiceMonitorWorker>.Instance, maintenance: _maintenance);
        _maintenance.Active = true;

        await worker.PollOnceAsync(Ct);
        await worker.WhenRecoveriesIdleAsync();

        _metrics.Verify(m => m.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServiceDown)), Times.Once);
        manager.Verify(m => m.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);

        _maintenance.Active = false;
        await worker.PollOnceAsync(Ct);
        await worker.WhenRecoveriesIdleAsync();
        manager.Verify(m => m.RestartService("Svc", It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task AppPoolDownDuringMaintenance_IsRecordedButNotStarted()
    {
        var pools = new Mock<IIisApplicationPoolManager>();
        pools.Setup(p => p.GetApplicationPoolStatusAsync("Pool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationPoolStatus(DateTime.UtcNow, "Pool", "Stopped", false));
        var options = new Mock<IOptionsMonitor<ApplicationPoolMonitorOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions
        {
            WatchedApplicationPools = ["Pool"],
            StartDelaySeconds = 0,
            StableStateDelaySeconds = 0,
        });
        var worker = new ApplicationPoolMonitorWorker(pools.Object, _metrics.Object, Alerts(), _portal.Object, options.Object, _time,
            NullLogger<ApplicationPoolMonitorWorker>.Instance, maintenance: _maintenance);
        _maintenance.Active = true;

        await worker.PollOnceAsync(Ct);

        _metrics.Verify(m => m.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ApplicationPoolDown)), Times.Once);
        pools.Verify(p => p.StartApplicationPoolAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MaintenanceStartingDuringTheAppPoolStartDelay_PreventsTheStart()
    {
        var pools = new Mock<IIisApplicationPoolManager>();
        pools.Setup(p => p.GetApplicationPoolStatusAsync("Pool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationPoolStatus(DateTime.UtcNow, "Pool", "Stopped", false));
        pools.Setup(p => p.StartApplicationPoolAsync("Pool", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var options = new Mock<IOptionsMonitor<ApplicationPoolMonitorOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions
        {
            WatchedApplicationPools = ["Pool"],
            StartDelaySeconds = 30,
            StableStateDelaySeconds = 0,
        });
        var worker = new ApplicationPoolMonitorWorker(pools.Object, _metrics.Object, Alerts(), _portal.Object, options.Object, _time,
            NullLogger<ApplicationPoolMonitorWorker>.Instance, maintenance: _maintenance);

        // The status query completes synchronously, so the poll is now waiting out StartDelay.
        var poll = worker.PollOnceAsync(Ct);
        poll.IsCompleted.Should().BeFalse();
        _maintenance.Active = true;
        _time.Advance(TimeSpan.FromSeconds(30));
        await poll;

        pools.Verify(p => p.StartApplicationPoolAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _metrics.Verify(m => m.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ApplicationPoolDown)), Times.Once);
    }

    [Fact]
    public async Task MaintenanceStartingDuringTheServiceRestartDelay_PreventsTheRestart()
    {
        var manager = new Mock<IWindowsServiceManager>();
        manager.Setup(m => m.GetServiceStatus("Svc")).Returns(
            new ServiceStatus(DateTime.UtcNow, "Svc", "Service", "Stopped", false, "Automatic"));
        manager.Setup(m => m.RestartService("Svc", It.IsAny<TimeSpan>())).Returns(true);
        var options = new Mock<IOptionsMonitor<ServiceMonitorOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            WatchedServices = ["Svc"],
            RestartDelaySeconds = 30,
            StableStateDelaySeconds = 0,
            StartupGraceSeconds = 0,
        });
        var worker = new ServiceMonitorWorker(manager.Object, _metrics.Object, Alerts(), _portal.Object, options.Object, _time,
            NullLogger<ServiceMonitorWorker>.Instance, maintenance: _maintenance);

        await worker.PollOnceAsync(Ct);
        _maintenance.Active = true;
        // The recovery task registers its delay on the thread pool: step the clock until it is done.
        var idle = worker.WhenRecoveriesIdleAsync();
        for (var i = 0; i < 500 && !idle.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, Ct);
        }
        await idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        manager.Verify(m => m.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    // ── Portal remote config ───────────────────────────────────────────────

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData("2026-01-08T00:00:00Z", null, true)]
    [InlineData("2025-12-01T00:00:00Z", null, true)] // past: ends the window
    [InlineData("2026-01-08T01:00:00Z", "Maintenance.UntilUtc must be at most 7 days in the future", false)]
    [InlineData("next tuesday", "Maintenance.UntilUtc must be an ISO-8601 timestamp", false)]
    public void RemoteConfig_MaintenanceIsLimitedToSevenDays(string until, string? expectedError, bool valid)
    {
        var config = new RemoteAgentConfigDto { Maintenance = new RemoteMaintenanceConfig { UntilUtc = until, Reason = "patch" } };

        RemoteConfigValidator.Validate(config, out var error, Now).Should().Be(valid);
        if (expectedError is not null)
            error.Should().Be(expectedError);
    }

    [Fact]
    public void RemoteConfig_RejectsOverlongReason()
    {
        var config = new RemoteAgentConfigDto { Maintenance = new RemoteMaintenanceConfig { Reason = new string('x', 501) } };

        RemoteConfigValidator.Validate(config, out var error, Now).Should().BeFalse();
        error.Should().Contain("Maintenance.Reason");
    }

    [Fact]
    public void RemoteConfig_MaintenanceIsWrittenToTheOverlayAndDeserializedFromCamelCase()
    {
        var config = JsonSerializer.Deserialize<RemoteAgentConfigDto>(
            """{ "maintenance": { "untilUtc": "2026-01-01T06:00:00+03:00", "reason": " Patch Tuesday " } }""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        var overlay = JsonNode.Parse(ApplyConfigurationHandler.BuildConfigFileJson(config))!;

        overlay["Maintenance"]!["Until"]!.GetValue<string>().Should().Be("2026-01-01T03:00:00.0000000Z");
        overlay["Maintenance"]!["Reason"]!.GetValue<string>().Should().Be("Patch Tuesday");
    }

    [Fact]
    public void RemoteConfig_GrpcMaintenanceIsMapped()
    {
        var dto = GrpcPortalClient.MapRemoteConfig(new WatchTower.Portal.Contracts.Protobuf.RemoteAgentConfig
        {
            Maintenance = new WatchTower.Portal.Contracts.Protobuf.RemoteMaintenanceConfig { UntilUtc = "2026-01-01T06:00:00Z" },
        });

        dto.Maintenance!.UntilUtc.Should().Be("2026-01-01T06:00:00Z");
        dto.Maintenance.Reason.Should().BeNull();
        GrpcPortalClient.MapRemoteConfig(new WatchTower.Portal.Contracts.Protobuf.RemoteAgentConfig()).Maintenance.Should().BeNull();
    }

    [Fact]
    public void Telemetry_CarriesMaintenanceOnTheWire()
    {
        var telemetry = new AgentTelemetryDto("1.6.0", "SRV-01", "Windows", "t", 1, "Idle", null, null,
            new MaintenanceTelemetryDto(true, "2026-01-01T06:00:00.0000000Z", "patching"));

        var json = JsonSerializer.Serialize(telemetry, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"maintenance\":{\"active\":true,\"untilUtc\":\"2026-01-01T06:00:00.0000000Z\",\"reason\":\"patching\"}");
    }

    // ── CLI and console ────────────────────────────────────────────────────

    [Fact]
    public async Task Cli_StartAndStopWriteTheOperatorSettings()
    {
        Assert.SkipUnless(DataDirectorySecurity.IsPrivileged(), "The maintenance command requires an elevated prompt.");
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var output = new StringWriter();

        var code = await WatchTowerCli.RunAsync(
            ["maintenance", "start", "--minutes", "90", "--reason", "Patching", "--data-dir", dir.Paths.DataDirectory], output, output);

        code.Should().Be(0, output.ToString());
        var settings = JsonNode.Parse(File.ReadAllText(dir.Paths.SettingsFilePath))!;
        MaintenanceState.TryParseUntil(settings["Maintenance"]!["Until"]!.GetValue<string>(), out var until).Should().BeTrue();
        until.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(90), TimeSpan.FromMinutes(1));
        settings["Maintenance"]!["Reason"]!.GetValue<string>().Should().Be("Patching");

        code = await WatchTowerCli.RunAsync(["maintenance", "stop", "--data-dir", dir.Paths.DataDirectory], output, output);

        code.Should().Be(0, output.ToString());
        JsonNode.Parse(File.ReadAllText(dir.Paths.SettingsFilePath))!["Maintenance"].Should().BeNull();
        (await WatchTowerCli.RunAsync(["maintenance", "start", "--minutes", "20000", "--data-dir", dir.Paths.DataDirectory], output, output))
            .Should().Be(1);
    }

    [Fact]
    public async Task ConsoleApi_StartsAndStopsMaintenance_AndReportsItInStatus()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var status = await client.GetFromJsonAsync<JsonElement>("api/status", Ct);
        status.GetProperty("maintenance").GetProperty("active").GetBoolean().Should().BeFalse();
        status.GetProperty("alerts").GetArrayLength().Should().Be(0);
        status.GetProperty("metrics").GetProperty("endpoints").GetArrayLength().Should().Be(0);

        (await client.PostAsJsonAsync("api/actions/maintenance", new { minutes = 0 }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var started = await client.PostAsJsonAsync("api/actions/maintenance", new { minutes = 60, reason = "Patching" }, Ct);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("maintenance").GetProperty("active").GetBoolean()
            .Should().BeTrue();

        status = await client.GetFromJsonAsync<JsonElement>("api/status", Ct);
        var maintenance = status.GetProperty("maintenance");
        maintenance.GetProperty("active").GetBoolean().Should().BeTrue();
        maintenance.GetProperty("reason").GetString().Should().Be("Patching");
        maintenance.GetProperty("until").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));

        var stopped = await client.PostAsJsonAsync("api/actions/maintenance/stop", new { }, Ct);
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>("api/status", Ct)).GetProperty("maintenance").GetProperty("active").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public void Validator_RejectsUnreadableMaintenanceEnd()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Maintenance:Until"] = "tomorrow-ish" }).Build();

        StartupValidator.Validate(config).Errors.Should().Contain(e => e.StartsWith("Maintenance:Until"));
    }
}