using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly Mock<ILogger<ServiceMonitorWorker>> _logger = new();

    public ServiceMonitorWorkerTests()
    {
        // Zero intra-iteration delays so a single PollOnceAsync completes immediately.
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            PollIntervalSeconds = 1,
            MaxRestartAttempts = 3,
            RestartDelaySeconds = 0,
            RestartTimeoutSeconds = 1,
            StableStateDelaySeconds = 0,
            WatchedServices = new List<string> { "TestService" }
        });
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _emailService.Setup(e => e.IsConfigured).Returns(true);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
    }

    private static ServiceStatus Status(string state, bool running) =>
        new(DateTime.UtcNow, "TestService", "Test Service", state, running);

    [Fact]
    public async Task PollOnce_RunningService_ShouldStoreStatus()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Running", true));

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddServiceStatus(It.Is<ServiceStatus>(st => st.IsRunning)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollOnce_StoppedService_ShouldAttemptRestart()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _serviceManager.Verify(s => s.RestartService("TestService", It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_StoppedService_ShouldLogDownIncident()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ServiceDown && i.Description.Contains("Test Service"))), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RestartThatStaysUp_ShouldLogRestartedIncident()
    {
        // First read sees it stopped (triggers restart); the recheck sees it running.
        var calls = 0;
        _serviceManager.Setup(s => s.GetServiceStatus("TestService"))
            .Returns(() => System.Threading.Interlocked.Increment(ref calls) > 1
                ? Status("Running", true)
                : Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ServiceRestarted && i.Description.Contains("Test Service"))), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RestartThatImmediatelyDies_DoesNotReportSuccess()
    {
        // Reaches Running once but the stabilisation recheck still sees it stopped: a crash loop.
        _serviceManager.Setup(s => s.GetServiceStatus("TestService")).Returns(Status("Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServiceRestarted)), Times.Never);
    }

    [Fact]
    public async Task PollOnce_NoWatchedServices_ShouldNotQueryServices()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions { WatchedServices = new List<string>() });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _serviceManager.Verify(s => s.GetServiceStatus(It.IsAny<string>()), Times.Never);
    }

    private ServiceMonitorWorker CreateWorker() => new(
        _serviceManager.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        TimeProvider.System,
        _logger.Object);
}