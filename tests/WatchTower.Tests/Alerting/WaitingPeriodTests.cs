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
using WatchTower.Workers;

namespace WatchTower.Tests.Alerting;

/// <summary>
/// Per-alert waiting periods (<c>...SustainedSeconds</c>): a breach is pending, shown but not sent,
/// until it has lasted its period; one that clears first is never sent (and never "resolved").
/// </summary>
public class WaitingPeriodTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<IMetricsStore> _store = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly AlertManager _alerts;

    public WaitingPeriodTests() => _alerts = TestAlerts.Create(_store.Object, _time, _dispatcher);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private void Advance(int seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private static readonly AlertKey Queue = AlertKey.DiskQueue("0 C:");

    // ------------------------------------------------------------------ the alert manager

    [Fact]
    public async Task BreachShorterThanItsPeriod_SendsNothing_LeavesNoAlert_AndNoResolved()
    {
        var wait = TimeSpan.FromMinutes(5);
        for (var s = 0; s < 240; s += 60)
        {
            (await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, wait, "Disk Queue High", Incident(), Ct))
                .Should().BeFalse("the breach has not lasted 5 minutes yet");
            Advance(60);
        }

        var pending = _alerts.GetPendingAlerts().Should().ContainSingle().Subject;
        pending.Key.Should().Be(Queue.Value);
        pending.Severity.Should().Be(AlertSeverity.Warning);
        pending.AlertsAt.Should().Be(new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));

        _alerts.ClearBreach(Queue); // back under the threshold after 4 minutes
        Advance(3600);
        await _alerts.ResolveAsync(Queue, ct: Ct);

        _dispatcher.Sent.Should().BeEmpty("nothing was raised, so there is nothing to resolve either");
        _alerts.GetOpenAlerts().Should().BeEmpty();
        _alerts.GetPendingAlerts().Should().BeEmpty();
        _store.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);
        _alerts.GetShortBreaches(Now.AddDays(-1)).Should().ContainSingle()
            .Which.Should().Be(new ShortBreachSummary("diskqueue", "Disk queue", 1, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public async Task BreachLongerThanItsPeriod_RaisesExactlyOnce_WhenThePeriodEnds()
    {
        var raisedAt = new List<int>();
        for (var s = 0; s <= 600; s += 30)
        {
            if (await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, TimeSpan.FromSeconds(300), "Disk Queue High", Incident(), Ct))
                raisedAt.Add(s);
            Advance(30);
        }

        raisedAt.First().Should().Be(300, "the alert is raised once the breach has lasted 300 s, not before");
        _dispatcher.Sent.Should().ContainSingle().Which.Kind.Should().Be(AlertNotificationKind.Opened);
        _store.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Once);
        _alerts.GetPendingAlerts().Should().BeEmpty("a raised alert is no longer pending");
        _alerts.IsOpen(Queue).Should().BeTrue();
    }

    [Fact]
    public async Task ZeroPeriod_RaisesOnTheFirstReading()
    {
        (await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, TimeSpan.Zero, "Disk Queue High", Incident(), Ct))
            .Should().BeTrue();

        _dispatcher.Sent.Should().ContainSingle();
        _alerts.GetPendingAlerts().Should().BeEmpty();
    }

    [Fact]
    public async Task OneReadingUnderTheThreshold_StartsTheWaitOver()
    {
        var wait = TimeSpan.FromSeconds(300);
        await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, wait, "q", Incident(), Ct);
        Advance(240);
        _alerts.ClearBreach(Queue);
        Advance(30);
        await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, wait, "q", Incident(), Ct);
        Advance(240);

        (await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, wait, "q", Incident(), Ct))
            .Should().BeFalse("the wait restarted 240 s ago, after the reading under the threshold");
        _dispatcher.Sent.Should().BeEmpty();
        _alerts.GetPendingAlerts().Single().Since.Should().Be(new DateTime(2026, 1, 1, 0, 4, 30, DateTimeKind.Utc));
    }

    [Fact]
    public async Task AnOpenAlert_StaysOpenWithoutANewWait_WhenTheReadingDipsAndComesBack()
    {
        var wait = TimeSpan.FromSeconds(300);
        await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, TimeSpan.Zero, "q", Incident(), Ct);
        Advance(30);
        _alerts.ClearBreach(Queue); // one good reading: not long enough to resolve (RecoveryWindow)
        Advance(30);

        (await _alerts.RaiseAfterAsync(Queue, AlertSeverity.Warning, wait, "q", Incident(), Ct))
            .Should().BeTrue("the alert is still open, so there is nothing to wait for");
        _alerts.GetPendingAlerts().Should().BeEmpty();
        _dispatcher.Sent.Should().ContainSingle("the open alert is not notified again");
    }

    [Fact]
    public async Task Escalation_EachSeverityWaitsItsOwnPeriod_FromWhenItWasFirstReached()
    {
        var disk = AlertKey.Disk(@"C:\");
        var wait = TimeSpan.FromSeconds(600);

        await _alerts.RaiseAfterAsync(disk, AlertSeverity.Warning, wait, "Disk Warning", Incident(), Ct);
        Advance(600);
        (await _alerts.RaiseAfterAsync(disk, AlertSeverity.Warning, wait, "Disk Warning", Incident(), Ct)).Should().BeTrue();
        Advance(100);

        // Critical from t=700: its own 600 s run from then, although the drive is already alerting.
        (await _alerts.RaiseAfterAsync(disk, AlertSeverity.Critical, wait, "Disk Critical", Incident(), Ct)).Should().BeFalse();
        _alerts.GetPendingAlerts().Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.Critical);
        Advance(599);
        (await _alerts.RaiseAfterAsync(disk, AlertSeverity.Critical, wait, "Disk Critical", Incident(), Ct)).Should().BeFalse();
        Advance(1);
        (await _alerts.RaiseAfterAsync(disk, AlertSeverity.Critical, wait, "Disk Critical", Incident(), Ct)).Should().BeTrue();

        _dispatcher.Sent.Select(n => (n.Kind, n.Severity)).Should().Equal(
            (AlertNotificationKind.Opened, AlertSeverity.Warning),
            (AlertNotificationKind.Opened, AlertSeverity.Critical));
        _alerts.GetPendingAlerts().Should().BeEmpty();
    }

    [Fact]
    public async Task FallingBackBelowAHigherSeverity_EndsItsWait_ButNotTheLowerOnes()
    {
        var disk = AlertKey.Disk(@"C:\");
        var wait = TimeSpan.FromSeconds(600);

        await _alerts.RaiseAfterAsync(disk, AlertSeverity.Warning, wait, "Disk Warning", Incident(), Ct);
        Advance(300);
        await _alerts.RaiseAfterAsync(disk, AlertSeverity.Critical, wait, "Disk Critical", Incident(), Ct);
        Advance(200);
        // Back to the warning range at t=500: the critical wait ends silently, the warning one goes on.
        await _alerts.RaiseAfterAsync(disk, AlertSeverity.Warning, wait, "Disk Warning", Incident(), Ct);
        _alerts.GetPendingAlerts().Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.Warning);
        Advance(100);

        (await _alerts.RaiseAfterAsync(disk, AlertSeverity.Warning, wait, "Disk Warning", Incident(), Ct))
            .Should().BeTrue("the warning level has been reached for 600 s");
        _dispatcher.Sent.Should().ContainSingle().Which.Severity.Should().Be(AlertSeverity.Warning);
    }

    [Fact]
    public async Task RemovingWhatWasWatched_ForgetsItsPendingBreach()
    {
        _alerts.ObserveBreach(AlertKey.Service("Spooler"), AlertSeverity.Critical, TimeSpan.FromMinutes(2), "Service Down: Spooler");
        _alerts.ObserveBreach(AlertKey.Service("W3SVC"), AlertSeverity.Critical, TimeSpan.FromMinutes(2), "Service Down: W3SVC");
        _alerts.ObserveBreach(AlertKey.Disk(@"E:\"), AlertSeverity.Critical, TimeSpan.FromMinutes(2), "Disk Critical");

        await _alerts.RetireUnconfiguredAsync(AlertKey.ServicePrefix, [AlertKey.Service("W3SVC")], "removed", Ct);
        await _alerts.RetireAsync(AlertKey.Disk(@"E:\"), "gone", Ct);

        _alerts.GetPendingAlerts().Select(p => p.Key).Should().Equal("svc:W3SVC");
        _alerts.GetShortBreaches(Now.AddDays(-1)).Should().BeEmpty("a removed resource did not recover");
    }

    [Fact]
    public async Task ShortBreaches_AreSummarisedPerKind_WithTheLongest()
    {
        var cpu = TimeSpan.FromMinutes(5);
        foreach (var lasted in new[] { 20, 90, 45 })
        {
            await _alerts.RaiseAfterAsync(AlertKey.Cpu, AlertSeverity.Critical, cpu, "CPU", Incident(), Ct);
            Advance(lasted);
            _alerts.ClearBreach(AlertKey.Cpu);
            Advance(10);
        }
        _alerts.ObserveBreach(Queue, AlertSeverity.Warning, cpu, "q");
        Advance(130);
        _alerts.ClearBreach(Queue);
        _alerts.ClearBreach(AlertKey.Ram); // nothing was waiting: not counted

        _alerts.GetShortBreaches(Now.AddDays(-1)).Should().Equal(
            new ShortBreachSummary("cpu", "CPU", 3, TimeSpan.FromSeconds(90)),
            new ShortBreachSummary("diskqueue", "Disk queue", 1, TimeSpan.FromSeconds(130)));
        _alerts.GetShortBreaches(Now.AddSeconds(1)).Should().BeEmpty("only breaches that ended since then count");
    }

    // ------------------------------------------------------------------ the monitors

    private DiskMonitorWorker DiskWorker(DiskMonitorOptions options, Mock<ISystemMetricsProvider> provider) => new(
        provider.Object, _store.Object, _alerts, new TestOptionsMonitor<DiskMonitorOptions>(options), _time,
        NullLogger<DiskMonitorWorker>.Instance);

    [Fact]
    public async Task DiskQueue_WithTheDefaults_AlertsOnlyAfterFiveMinutes_AndResolvesAsBefore()
    {
        var provider = new Mock<ISystemMetricsProvider>();
        var queue = 5.0;
        provider.Setup(p => p.GetDiskMetrics()).Returns(() => [new DiskMetric(Now, @"C:\", 100, 60, 40)]);
        provider.Setup(p => p.GetDiskQueueMetrics()).Returns(() => [new DiskQueueMetric(Now, "0 C:", queue)]);
        var worker = DiskWorker(new DiskMonitorOptions(), provider); // defaults: QueueSustainedSeconds 300

        for (var s = 0; s < 300; s += 60)
        {
            await worker.PollOnceAsync(Ct);
            Advance(60);
        }
        _dispatcher.Sent.Should().BeEmpty("the queue has been high for 4 minutes only");
        var pending = _alerts.GetPendingAlerts().Should().ContainSingle().Subject;
        pending.Title.Should().StartWith("Disk Queue High: 0 C:");
        pending.AlertAfter.Should().Be(TimeSpan.FromMinutes(5));

        await worker.PollOnceAsync(Ct); // t = 300 s
        _dispatcher.Sent.Should().ContainSingle().Which.Title.Should().StartWith("Disk Queue High");

        queue = 0.5;
        for (var s = 0; s <= 300; s += 60)
        {
            Advance(60);
            await worker.PollOnceAsync(Ct);
        }
        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
    }

    [Fact]
    public async Task DiskQueueSpike_ThatClearsWithinItsPeriod_IsNeverSent()
    {
        var provider = new Mock<ISystemMetricsProvider>();
        var queue = 9.0;
        provider.Setup(p => p.GetDiskMetrics()).Returns(() => [new DiskMetric(Now, @"C:\", 100, 60, 40)]);
        provider.Setup(p => p.GetDiskQueueMetrics()).Returns(() => [new DiskQueueMetric(Now, "0 C:", queue)]);
        var worker = DiskWorker(new DiskMonitorOptions(), provider);

        await worker.PollOnceAsync(Ct);
        Advance(120);
        await worker.PollOnceAsync(Ct);
        queue = 0;
        for (var i = 0; i < 20; i++)
        {
            Advance(60);
            await worker.PollOnceAsync(Ct);
        }

        _dispatcher.Sent.Should().BeEmpty("no opened and no resolved notice for a two-minute spike");
        _alerts.GetPendingAlerts().Should().BeEmpty();
        _alerts.GetShortBreaches(Now.AddDays(-1)).Single().Kind.Should().Be("diskqueue");
    }

    [Fact]
    public async Task EachAlertType_WaitsItsOwnPeriod()
    {
        var provider = new Mock<ISystemMetricsProvider>();
        provider.Setup(p => p.GetDiskMetrics()).Returns(() => [new DiskMetric(Now, @"C:\", 100, 5, 95)]);
        provider.Setup(p => p.GetDiskQueueMetrics()).Returns(() => [new DiskQueueMetric(Now, "0 C:", 9)]);
        provider.Setup(p => p.GetNetworkMetrics()).Returns(() => [new NetworkMetric(Now, "Ethernet", 4)]);
        var disk = DiskWorker(new DiskMonitorOptions { SustainedSeconds = 600, QueueSustainedSeconds = 120 }, provider);
        var network = new NetworkMonitorWorker(provider.Object, _store.Object, _alerts,
            new TestOptionsMonitor<NetworkMonitorOptions>(new NetworkMonitorOptions { SustainedSeconds = 300 }), _time,
            NullLogger<NetworkMonitorWorker>.Instance);

        var firstSent = new Dictionary<string, int>();
        for (var s = 0; s <= 660; s += 60)
        {
            await disk.PollOnceAsync(Ct);
            await network.PollOnceAsync(Ct);
            foreach (var n in _dispatcher.Sent)
                firstSent.TryAdd(n.AlertKey, s);
            Advance(60);
        }

        firstSent.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["diskqueue:0 C:"] = 120,
            ["net:Ethernet"] = 300,
            [@"disk:C:\"] = 600,
        });
        _dispatcher.Sent.Should().HaveCount(3, "each alert is sent once");
    }

    [Fact]
    public async Task Cpu_KeepsItsSustainedSeconds_AsTheWaitingPeriod()
    {
        var provider = new Mock<ISystemMetricsProvider>();
        provider.Setup(p => p.GetCpuUsage()).Returns(99.5f);
        provider.Setup(p => p.GetTopCpuProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProcessInfo>());
        var worker = new CpuMonitorWorker(provider.Object, _store.Object, _alerts, new NullPortalEventPublisher(),
            new TestOptionsMonitor<CpuMonitorOptions>(new CpuMonitorOptions { SustainedSeconds = 60 }), _time,
            NullLogger<CpuMonitorWorker>.Instance);

        await worker.PollOnceAsync(Ct);
        Advance(55);
        await worker.PollOnceAsync(Ct);
        _dispatcher.Sent.Should().BeEmpty();
        _alerts.GetPendingAlerts().Single().Key.Should().Be("cpu");
        _store.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.CpuSpikeDetected)), Times.Once,
            "the spike is still recorded at once");

        Advance(5);
        await worker.PollOnceAsync(Ct);
        _dispatcher.Sent.Should().ContainSingle().Which.AlertKey.Should().Be("cpu");
    }

    // ------------------------------------------------------------------ endpoints

    private readonly Mock<IEndpointProbe> _probe = new();

    private EndpointMonitorWorker EndpointWorker(EndpointMonitorOptions options) => new(
        _probe.Object, _store.Object, _alerts, new TestOptionsMonitor<EndpointMonitorOptions>(options), _time,
        NullLogger<EndpointMonitorWorker>.Instance);

    private void ProbeReturns(EndpointState state) =>
        _probe.Setup(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointProbeResult(state, state == EndpointState.Down ? "HTTP 503" : "HTTP 200", 12));

    private async Task<int?> FirstAlertSecond(EndpointMonitorWorker worker, int until)
    {
        for (var s = 0; s <= until; s += (int)EndpointMonitorWorker.Tick.TotalSeconds)
        {
            await worker.PollOnceAsync(Ct);
            if (_dispatcher.Sent.Count > 0)
                return s;
            _time.Advance(EndpointMonitorWorker.Tick);
        }
        return null;
    }

    [Fact]
    public async Task Endpoint_OneFailureIsEnoughByCount_ButStillWaitsTheDefaultTwoMinutes()
    {
        ProbeReturns(EndpointState.Down);
        var worker = EndpointWorker(new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "site", Target = "https://example.com", IntervalSeconds = 60, FailuresBeforeAlert = 1 }],
        });

        (await FirstAlertSecond(worker, 300)).Should().Be(120, "checks fail at 0, 60 and 120 s; 120 s is the default waiting period");
    }

    [Fact]
    public async Task Endpoint_NeedsItsFailureCountToo_AfterTheWaitingPeriod()
    {
        ProbeReturns(EndpointState.Down);
        var worker = EndpointWorker(new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "site", Target = "https://example.com", IntervalSeconds = 60, FailuresBeforeAlert = 4 }],
            EndpointDefaults = new EndpointDefaultsOptions { SustainedSeconds = 30 },
        });

        (await FirstAlertSecond(worker, 400)).Should().Be(180, "the fourth failed check in a row is at 180 s");
    }

    [Fact]
    public async Task Endpoint_OwnSustainedSeconds_OverridesTheDefault_AndASingleFailureNeverAlerts()
    {
        var worker = EndpointWorker(new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "site", Target = "https://example.com", IntervalSeconds = 60, FailuresBeforeAlert = 1, SustainedSeconds = 300 }],
        });

        ProbeReturns(EndpointState.Down);
        await worker.PollOnceAsync(Ct);
        _alerts.GetPendingAlerts().Single().AlertAfter.Should().Be(TimeSpan.FromSeconds(300));
        _time.Advance(TimeSpan.FromSeconds(60));
        ProbeReturns(EndpointState.Up);
        await worker.PollOnceAsync(Ct);
        _alerts.GetPendingAlerts().Should().BeEmpty();

        ProbeReturns(EndpointState.Down);
        (await FirstAlertSecond(worker, 400)).Should().Be(360,
            "the new streak starts with the check 60 s later and must last the check's own 300 s");
        _dispatcher.Sent.Should().ContainSingle();
    }

    // ------------------------------------------------------------------ services and pools

    private readonly Mock<IWindowsServiceManager> _services = new();

    private ServiceMonitorWorker ServiceWorker(int sustainedSeconds = 120) => new(
        _services.Object, _store.Object, _alerts, new NullPortalEventPublisher(),
        new TestOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions
        {
            WatchedServices = ["Spooler"],
            StartupGraceSeconds = 0,
            RestartDelaySeconds = 0,
            StableStateDelaySeconds = 0,
            MaxRestartAttempts = 3,
            SustainedSeconds = sustainedSeconds,
        }), _time, NullLogger<ServiceMonitorWorker>.Instance);

    private ServiceStatus Service(bool running) =>
        new(Now, "Spooler", "Print Spooler", running ? "Running" : "Stopped", running, "Automatic");

    private static async Task PollAsync(ServiceMonitorWorker worker)
    {
        await worker.PollOnceAsync(Ct);
        await worker.WhenRecoveriesIdleAsync();
    }

    [Fact]
    public async Task Service_RestartedByTheAgent_IsRecordedButNeverSent()
    {
        var running = false;
        _services.Setup(s => s.GetServiceStatus("Spooler")).Returns(() => Service(running));
        _services.Setup(s => s.RestartService("Spooler", It.IsAny<TimeSpan>())).Returns(() => running = true);
        var worker = ServiceWorker();

        await PollAsync(worker);
        Advance(30);
        await PollAsync(worker);

        _dispatcher.Sent.Should().BeEmpty();
        _alerts.GetOpenAlerts().Should().BeEmpty();
        _alerts.GetPendingAlerts().Should().BeEmpty();
        _store.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServiceRestarted)), Times.Once,
            "the successful restart is still recorded (and published as an event)");
        _alerts.GetShortBreaches(Now.AddDays(-1)).Single().Kind.Should().Be("svc");
    }

    [Fact]
    public async Task Service_StillDownAfterItsPeriod_AlertsOnce()
    {
        _services.Setup(s => s.GetServiceStatus("Spooler")).Returns(() => Service(false));
        _services.Setup(s => s.RestartService("Spooler", It.IsAny<TimeSpan>())).Returns(false);
        var worker = ServiceWorker();

        var firstSent = (int?)null;
        for (var s = 0; s <= 240; s += 30)
        {
            await PollAsync(worker);
            if (firstSent is null && _dispatcher.Sent.Count > 0)
                firstSent = s;
            if (s == 60)
            {
                _dispatcher.Sent.Should().BeEmpty("three restarts failed, but the service has been down for 60 s only");
                _alerts.GetPendingAlerts().Single().Key.Should().Be("svc:Spooler");
            }
            Advance(30);
        }

        firstSent.Should().Be(120);
        _dispatcher.Sent.Should().ContainSingle().Which.Title.Should().Be("Service Down: Spooler");
        _alerts.GetPendingAlerts().Should().BeEmpty();
    }

    [Fact]
    public async Task Service_ZeroPeriod_AlertsAsSoonAsTheRestartsFailed()
    {
        _services.Setup(s => s.GetServiceStatus("Spooler")).Returns(() => Service(false));
        _services.Setup(s => s.RestartService("Spooler", It.IsAny<TimeSpan>())).Returns(false);
        var worker = ServiceWorker(sustainedSeconds: 0);

        for (var i = 0; i < 3; i++)
        {
            _dispatcher.Sent.Should().BeEmpty();
            await PollAsync(worker);
            Advance(30);
        }

        _dispatcher.Sent.Should().ContainSingle("the third failed restart reaches MaxRestartAttempts");
    }

    [Fact]
    public async Task AppPool_StillDownAfterItsPeriod_Alerts_AndOneStartedByTheAgent_IsNotSent()
    {
        var pools = new Mock<IIisApplicationPoolManager>();
        var started = false;
        pools.Setup(p => p.GetApplicationPoolStatusAsync("Api", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationPoolStatus(Now, "Api", started ? "Started" : "Stopped", started));
        pools.Setup(p => p.StartApplicationPoolAsync("Api", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var worker = new ApplicationPoolMonitorWorker(pools.Object, _store.Object, _alerts, new NullPortalEventPublisher(),
            new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions
            {
                WatchedApplicationPools = ["Api"],
                StartDelaySeconds = 0,
                StableStateDelaySeconds = 0,
                MaxStartAttempts = 1,
                SustainedSeconds = 90,
            }), _time, NullLogger<ApplicationPoolMonitorWorker>.Instance);

        for (var s = 0; s < 90; s += 30)
        {
            await worker.PollOnceAsync(Ct);
            Advance(30);
        }
        _dispatcher.Sent.Should().BeEmpty("down for 60 s, under the 90 s waiting period");
        await worker.PollOnceAsync(Ct);
        _dispatcher.Sent.Should().ContainSingle().Which.Title.Should().Be("IIS App Pool Down: Api");

        // An outage the agent fixes itself goes without a word.
        pools.Setup(p => p.StartApplicationPoolAsync("Api", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => started = true);
        Advance(30);
        await worker.PollOnceAsync(Ct); // started: resolves the open alert, as before
        started = false;
        Advance(30);
        await worker.PollOnceAsync(Ct); // down again, started again at once
        _dispatcher.Sent.Select(n => n.Kind).Should().Equal(AlertNotificationKind.Opened, AlertNotificationKind.Resolved);
        _alerts.GetPendingAlerts().Should().BeEmpty();
    }

    private Incident Incident() => new(Now, IncidentType.DiskQueueHigh, "test");
}