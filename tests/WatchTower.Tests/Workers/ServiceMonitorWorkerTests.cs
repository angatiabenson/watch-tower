using FluentAssertions;
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
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            PollIntervalSeconds = 1,
            MaxRestartAttempts = 3,
            RestartDelaySeconds = 0,
            RestartTimeoutSeconds = 1,
            WatchedServices = new List<string> { "TestService" }
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });
    }

    [Fact]
    public async Task ExecuteAsync_RunningService_ShouldStoreStatus()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService"))
            .Returns(new ServiceStatus(DateTime.UtcNow, "TestService", "Test Service", "Running", true));

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddServiceStatus(It.Is<ServiceStatus>(st => st.IsRunning)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_StoppedService_ShouldAttemptRestart()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService"))
            .Returns(new ServiceStatus(DateTime.UtcNow, "TestService", "Test Service", "Stopped", false));
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(cts.Token);
        await Task.Delay(2000);
        await worker.StopAsync(CancellationToken.None);

        _serviceManager.Verify(s => s.RestartService("TestService", It.IsAny<TimeSpan>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulRestart_ShouldLogIncident()
    {
        // No stabilisation delay in the test, and the service is reported Running once the
        // recheck happens so it counts as a genuine (stable) recovery.
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            PollIntervalSeconds = 1,
            MaxRestartAttempts = 3,
            RestartDelaySeconds = 0,
            RestartTimeoutSeconds = 1,
            StableStateDelaySeconds = 0,
            WatchedServices = new List<string> { "TestService" }
        });

        var calls = 0;
        _serviceManager.Setup(s => s.GetServiceStatus("TestService"))
            .Returns(() =>
            {
                // First read sees it stopped (triggers restart); the post-restart recheck
                // and subsequent reads see it running.
                var running = System.Threading.Interlocked.Increment(ref calls) > 1;
                return new ServiceStatus(DateTime.UtcNow, "TestService", "Test Service",
                    running ? "Running" : "Stopped", running);
            });
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>())).Returns(true);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(cts.Token);
        await Task.Delay(2000);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ServiceRestarted &&
            i.Description.Contains("Test Service"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_NoWatchedServices_ShouldNotQueryServices()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions
        {
            PollIntervalSeconds = 1,
            RestartTimeoutSeconds = 1,
            WatchedServices = new List<string>()
        });

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _serviceManager.Verify(s => s.GetServiceStatus(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StoppedService_ShouldLogDownIncident()
    {
        _serviceManager.Setup(s => s.GetServiceStatus("TestService"))
            .Returns(
                new ServiceStatus(DateTime.UtcNow, "TestService", "Test Service", "Stopped", false)
            );
        _serviceManager.Setup(s => s.RestartService("TestService", It.IsAny<TimeSpan>()))
            .Returns(true);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(cts.Token);
        await Task.Delay(2000);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(
            s => s.AddIncident(
                It.Is<Incident>(i =>
                    i.Type == IncidentType.ServiceDown
                    && i.Description.Contains("Test Service")
                )
            ),
            Times.AtLeastOnce
        );
    }

    private ServiceMonitorWorker CreateWorker() => new(
        _serviceManager.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        _logger.Object);
}
