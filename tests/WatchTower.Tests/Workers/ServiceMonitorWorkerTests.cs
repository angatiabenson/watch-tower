using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class ServiceMonitorWorkerTests
{
    private readonly Mock<IWindowsServiceManager> _serviceManager = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<ServiceMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<ILogger<ServiceMonitorWorker>> _logger = new();

    public ServiceMonitorWorkerTests()
    {
        // Zero intra-iteration delays and no startup grace so a poll recovers immediately.
        UseOptions();
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _emailService.Setup(e => e.IsConfigured).Returns(true);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
    }

    private void UseOptions(int startupGraceSeconds = 0, int restartDelaySeconds = 0, params string[] services) =>
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            PollIntervalSeconds = 1,
            MaxRestartAttempts = 3,
            RestartDelaySeconds = restartDelaySeconds,
            RestartTimeoutSeconds = 1,
            StableStateDelaySeconds = 0,
            StartupGraceSeconds = startupGraceSeconds,
            WatchedServices = services.Length > 0 ? services.ToList() : new List<string> { "TestService" }
        });

    private ServiceStatus Status(string state, bool running, string service = "TestService", string? startType = "Automatic") =>
        new(_time.GetUtcNow().UtcDateTime, service, "Test Service", state, running, startType);

    /// <summary>Polls once and waits for any recovery the poll started.</summary>
    private static async Task PollAsync(ServiceMonitorWorker worker)
    {
        await worker.PollOnceAsync(CancellationToken.None);
        await worker.WhenRecoveriesIdleAsync();
    }

    [Fact]
    public async Task PollOnce_RunningService_ShouldStoreStatus()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Running", true));

        await PollAsync(CreateWorker());

        _metricsStore.Verify(s => s.AddServiceStatus(It.Is<ServiceStatus>(st => st.IsRunning)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollOnce_StoppedService_ShouldAttemptRestart()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await PollAsync(CreateWorker());

        _serviceManager.Verify(s => s.RestartService("TestService", It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_StoppedService_ShouldLogDownIncident()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await PollAsync(CreateWorker());

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ServiceDown && i.Description.Contains("Test Service"))), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RestartThatStaysUp_ShouldLogRestartedIncident()
    {
        // First read sees it stopped (triggers restart); the recheck sees it running.
        var calls = 0;
        _serviceManager.Setup(s => s.GetServiceStatus("TestService"))
            .Returns(() => Interlocked.Increment(ref calls) > 1
                ? Status("Running", true)
                : Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await PollAsync(CreateWorker());

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ServiceRestarted && i.Description.Contains("Test Service"))), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RestartThatImmediatelyDies_DoesNotReportSuccess()
    {
        // Reaches Running once but the stabilisation recheck still sees it stopped: a crash loop.
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await PollAsync(CreateWorker());

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServiceRestarted)), Times.Never);
    }

    [Fact]
    public async Task PollOnce_NoWatchedServices_ShouldNotQueryServices()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions { WatchedServices = new List<string>() });

        await PollAsync(CreateWorker());

        _serviceManager.Verify(s => s.GetServiceStatus(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("StartPending")]
    [InlineData("ContinuePending")]
    public async Task PollOnce_TransitionalState_IsNotTreatedAsDown(string state)
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status(state, false));

        await PollAsync(CreateWorker());

        _metricsStore.Verify(s => s.AddServiceStatus(It.Is<ServiceStatus>(st => st.Status == state)), Times.Once);
        _serviceManager.Verify(s => s.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_TransitionalStateThatSettlesStopped_IsRestartedNextPoll()
    {
        _serviceManager.SetupSequence(s => s.GetServiceStatus("TestService"))
            .Returns(Status("StartPending", false))
            .Returns(Status("Stopped", false))
            .Returns(Status("Running", true));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);
        var worker = CreateWorker();

        await PollAsync(worker);
        _serviceManager.Verify(s => s.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);

        await PollAsync(worker);
        _serviceManager.Verify(s => s.RestartService("TestService", It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_DisabledService_IsRecordedButNeverRestarted()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false, startType: "Disabled"));
        var worker = CreateWorker();

        await PollAsync(worker);
        await PollAsync(worker);

        _metricsStore.Verify(s => s.AddServiceStatus(It.Is<ServiceStatus>(st => st.StartType == "Disabled")), Times.Exactly(2));
        _serviceManager.Verify(s => s.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_DuringStartupGrace_RecordsButDoesNotRestart()
    {
        UseOptions(startupGraceSeconds: 120);
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);
        var worker = CreateWorker();

        await PollAsync(worker);
        _time.Advance(TimeSpan.FromSeconds(119));
        await PollAsync(worker);

        _metricsStore.Verify(s => s.AddServiceStatus(It.IsAny<ServiceStatus>()), Times.Exactly(2));
        _serviceManager.Verify(s => s.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);

        _time.Advance(TimeSpan.FromSeconds(1));
        await PollAsync(worker);

        _serviceManager.Verify(s => s.RestartService("TestService", It.IsAny<TimeSpan>()), Times.Once);
        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServiceDown)), Times.Once);
    }

    [Fact]
    public async Task PollOnce_SlowRecoveryOfOneService_DoesNotBlockChecksOfOthers()
    {
        // "Broken" waits RestartDelaySeconds on the fake clock, which never advances here, so its
        // recovery stays in flight for the whole test.
        UseOptions(restartDelaySeconds: 10, services: new[] { "Broken", "Healthy" });
        _serviceManager.Setup(s => s.GetServiceStatus("Broken")).Returns(Status("Stopped", false, "Broken"));
        _serviceManager.Setup(s => s.GetServiceStatus("Healthy")).Returns(Status("Running", true, "Healthy"));
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource();

        await worker.PollOnceAsync(cts.Token);
        await worker.PollOnceAsync(cts.Token);
        await worker.PollOnceAsync(cts.Token);

        _serviceManager.Verify(s => s.GetServiceStatus("Healthy"), Times.Exactly(3));
        // The in-flight recovery is not duplicated by later polls.
        _serviceManager.Verify(s => s.GetServiceStatus("Broken"), Times.Once);
        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServiceDown)), Times.Once);

        await cts.CancelAsync();
        await worker.WhenRecoveriesIdleAsync();
        _serviceManager.Verify(s => s.RestartService(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_OneServiceThrowing_DoesNotStopOthers()
    {
        UseOptions(services: new[] { "Exploding", "Healthy" });
        _serviceManager.Setup(s => s.GetServiceStatus("Exploding")).Throws(new InvalidOperationException("boom"));
        _serviceManager.Setup(s => s.GetServiceStatus("Healthy")).Returns(Status("Running", true, "Healthy"));

        await PollAsync(CreateWorker());

        _metricsStore.Verify(s => s.AddServiceStatus(It.Is<ServiceStatus>(st => st.ServiceName == "Healthy")), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RepeatedFailedRestarts_EscalateOncePerCooldown()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(false);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var worker = CreateWorker();

        for (var i = 0; i < 5; i++)
            await PollAsync(worker);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.ServiceRestartFailed)), Times.Once);
    }

    private ServiceMonitorWorker CreateWorker() => new(
        _serviceManager.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        _time,
        _logger.Object);
}