using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Services.Endpoints;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Alerting;

/// <summary>Alerts of services, pools and endpoints removed from the configuration are retired.</summary>
public class AlertRetirementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly Mock<IPortalEventPublisher> _portal = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly TestMaintenance _maintenance = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private AlertManager Alerts(IAlertStateStore? state = null) =>
        TestAlerts.Create(_metrics.Object, _time, _dispatcher, portal: _portal.Object, state: state, maintenance: _maintenance);

    private Incident Down(string what) => new(_time.GetUtcNow().UtcDateTime, IncidentType.ServiceRestartFailed, $"{what} failed");

    private Task Raise(IAlertManager alerts, AlertKey key) =>
        alerts.RaiseAsync(key, AlertSeverity.Critical, $"Down: {key}", Down(key.Value), Ct);

    [Fact]
    public async Task Retire_ClosesOnlyUnconfiguredKeysOfThePrefix_WithTheReason()
    {
        var alerts = Alerts();
        await Raise(alerts, AlertKey.Service("Kept"));
        await Raise(alerts, AlertKey.Service("Removed"));
        await Raise(alerts, AlertKey.AppPool("Removed"));
        _time.Advance(TimeSpan.FromMinutes(90));

        var retired = await alerts.RetireUnconfiguredAsync(AlertKey.ServicePrefix, [AlertKey.Service("KEPT")], "removed from config", Ct);

        retired.Should().Equal("svc:Removed");
        alerts.GetOpenAlerts().Select(a => a.Key).Should().BeEquivalentTo("svc:Kept", "pool:Removed");
        var notice = _dispatcher.Delivered.Last().Notification;
        notice.Should().BeEquivalentTo(new
        {
            Kind = AlertNotificationKind.Resolved,
            AlertKey = "svc:Removed",
            RetiredReason = "removed from config",
            Duration = TimeSpan.FromMinutes(90),
        });
        notice.Description.Should().StartWith("No longer monitored (removed from config); closed after 1 h 30 min.");
        _portal.Verify(p => p.PublishAlertEventAsync(
            It.Is<Incident>(i => i.Description.StartsWith("No longer monitored")), "svc:Removed", true, It.IsAny<CancellationToken>()), Times.Once);

        (await alerts.RetireUnconfiguredAsync(AlertKey.ServicePrefix, [AlertKey.Service("Kept")], "removed from config", Ct))
            .Should().BeEmpty("it is already closed");
    }

    [Fact]
    public async Task Retire_PersistsTheReason_AndARaiseAfterReAddingOpensAFreshAlert()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var alerts = Alerts(new SqliteAlertStateStore(dir.Paths.DatabasePath));
        var key = AlertKey.Endpoint("old-site");
        await Raise(alerts, key);

        await alerts.RetireUnconfiguredAsync(AlertKey.EndpointPrefix, [], "gone", Ct);

        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadAll().Single().Should().BeEquivalentTo(new
        {
            Key = "endpoint:old-site",
            RetiredReason = "gone",
            ResolvedAt = (DateTime?)_time.GetUtcNow().UtcDateTime,
        });

        await Raise(alerts, key);
        alerts.GetOpenAlerts().Single().RetiredReason.Should().BeNull();
    }

    [Fact]
    public async Task Retire_DuringMaintenance_IsHeldAndSentWhenTheWindowEnds()
    {
        var alerts = Alerts();
        await Raise(alerts, AlertKey.Service("Removed"));
        _maintenance.Active = true;

        await alerts.RetireUnconfiguredAsync(AlertKey.ServicePrefix, [], "removed", Ct);
        _dispatcher.Delivered.Should().ContainSingle();

        _maintenance.Active = false;
        await alerts.ProcessPendingAsync(Ct);
        _dispatcher.Delivered.Last().Notification.RetiredReason.Should().Be("removed");
    }

    [Fact]
    public void Webhook_CarriesTheRetiredReason()
    {
        var notification = new AlertNotification(AlertNotificationKind.Resolved, "svc:X", AlertSeverity.Critical, "Service Down: X",
            "No longer monitored", "SRV", _time.GetUtcNow().UtcDateTime, RetiredReason: "removed from Monitoring:Services:WatchedServices");

        var payload = JsonNode.Parse(new WebhookNotificationChannel(Mock.Of<IHttpClientFactory>()).BuildPayload(notification))!;

        payload["retiredReason"]!.GetValue<string>().Should().Be("removed from Monitoring:Services:WatchedServices");
    }

    // ── Workers reconcile at startup and after a configuration change ──────

    [Fact]
    public async Task EndpointWorker_RetiresAlertsOfRemovedChecks_AtStartupAndOnConfigChange()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        // Left open by the previous run, for a check that is no longer configured.
        var previousRun = Alerts(new SqliteAlertStateStore(dir.Paths.DatabasePath));
        await Raise(previousRun, AlertKey.Endpoint("deleted"));

        var probe = new Mock<IEndpointProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointProbeResult(EndpointState.Down, "HTTP 503", 1));
        var options = new TestOptionsMonitor<EndpointMonitorOptions>(new EndpointMonitorOptions
        {
            Endpoints =
            [
                new EndpointCheckOptions { Name = "a", Target = "https://a.example", FailuresBeforeAlert = 1 },
                new EndpointCheckOptions { Name = "b", Target = "https://b.example", FailuresBeforeAlert = 1 },
            ],
        });
        var alerts = Alerts(new SqliteAlertStateStore(dir.Paths.DatabasePath));
        var worker = new EndpointMonitorWorker(probe.Object, _metrics.Object, alerts, options, _time,
            NullLogger<EndpointMonitorWorker>.Instance);

        await worker.PollOnceAsync(Ct);

        alerts.GetOpenAlerts().Select(a => a.Key).Should().BeEquivalentTo("endpoint:a", "endpoint:b");

        // "b" is removed (and "a" made invalid) while both are down.
        options.CurrentValue = new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "a", Target = "not a url" }],
        };
        _time.Advance(TimeSpan.FromSeconds(5));
        await worker.PollOnceAsync(Ct);

        alerts.GetOpenAlerts().Should().BeEmpty();
        new SqliteAlertStateStore(dir.Paths.DatabasePath).LoadAll()
            .Should().OnlyContain(s => s.RetiredReason == EndpointMonitorWorker.RemovedReason);
    }

    [Fact]
    public async Task ServiceWorker_RetiresAlertsOfServicesNoLongerWatched()
    {
        var alerts = Alerts();
        await Raise(alerts, AlertKey.Service("Old"));
        await Raise(alerts, AlertKey.Service("Svc"));
        var manager = new Mock<IWindowsServiceManager>();
        manager.Setup(m => m.GetServiceStatus("Svc")).Returns(new ServiceStatus(DateTime.UtcNow, "Svc", "Svc", "Stopped", false, "Disabled"));
        var options = new TestOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions { WatchedServices = ["Svc"], StartupGraceSeconds = 0 });
        var worker = new ServiceMonitorWorker(manager.Object, _metrics.Object, alerts, _portal.Object, options, _time,
            NullLogger<ServiceMonitorWorker>.Instance);

        await worker.PollOnceAsync(Ct);
        alerts.GetOpenAlerts().Select(a => a.Key).Should().Equal("svc:Svc");

        options.CurrentValue = new ServiceMonitorOptions { WatchedServices = [] };
        await worker.PollOnceAsync(Ct);
        alerts.GetOpenAlerts().Should().BeEmpty("an empty watch list retires everything");
        _dispatcher.Delivered.Where(d => d.Notification.RetiredReason == ServiceMonitorWorker.RemovedReason)
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task AppPoolWorker_RetiresAlertsOfPoolsNoLongerWatched()
    {
        var alerts = Alerts();
        await Raise(alerts, AlertKey.AppPool("OldPool"));
        var pools = new Mock<IIisApplicationPoolManager>();
        pools.Setup(p => p.GetApplicationPoolStatusAsync("Pool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationPoolStatus(DateTime.UtcNow, "Pool", "Started", true));
        var options = new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions { WatchedApplicationPools = ["Pool"] });
        var worker = new ApplicationPoolMonitorWorker(pools.Object, _metrics.Object, alerts, _portal.Object, options, _time,
            NullLogger<ApplicationPoolMonitorWorker>.Instance);

        await worker.PollOnceAsync(Ct);

        alerts.GetOpenAlerts().Should().BeEmpty();
        _dispatcher.Delivered.Last().Notification.RetiredReason.Should().Be(ApplicationPoolMonitorWorker.RemovedReason);
    }
}