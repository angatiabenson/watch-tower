using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class ApplicationPoolMonitorWorkerTests
{
    private readonly Mock<IIisApplicationPoolManager> _applicationPoolManager = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<ApplicationPoolMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
    private readonly Mock<ILogger<ApplicationPoolMonitorWorker>> _logger = new();

    public ApplicationPoolMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions
        {
            PollIntervalSeconds = 1,
            MaxStartAttempts = 3,
            StartDelaySeconds = 0,
            StartTimeoutSeconds = 1,
            StableStateDelaySeconds = 0,
            WatchedApplicationPools = new List<string> { "DefaultAppPool" }
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });
    }

    [Fact]
    public async Task ExecuteAsync_StartedPool_ShouldStoreStatus()
    {
        _applicationPoolManager.Setup(s => s.GetApplicationPoolStatus("DefaultAppPool"))
            .Returns(new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Started", true));

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(
            s => s.AddApplicationPoolStatus(It.Is<ApplicationPoolStatus>(st => st.IsRunning)),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task ExecuteAsync_StoppedPool_ShouldAttemptStart()
    {
        _applicationPoolManager.SetupSequence(s => s.GetApplicationPoolStatus("DefaultAppPool"))
            .Returns(new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Stopped", false))
            .Returns(new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Started", true));
        _applicationPoolManager.Setup(
            s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>())
        ).Returns(true);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(cts.Token);
        await Task.Delay(2000);
        await worker.StopAsync(CancellationToken.None);

        _applicationPoolManager.Verify(
            s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulStart_ShouldLogIncident()
    {
        _applicationPoolManager.SetupSequence(s => s.GetApplicationPoolStatus("DefaultAppPool"))
            .Returns(new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Stopped", false))
            .Returns(new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Started", true));
        _applicationPoolManager.Setup(
            s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>())
        ).Returns(true);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(cts.Token);
        await Task.Delay(2000);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(
            s => s.AddIncident(
                It.Is<Incident>(i =>
                    i.Type == IncidentType.ApplicationPoolStarted
                    && i.Description.Contains("DefaultAppPool")
                )
            ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task ExecuteAsync_FailedStart_ShouldSendEmergencyEmail()
    {
        _applicationPoolManager.Setup(s => s.GetApplicationPoolStatus("DefaultAppPool"))
            .Returns(new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Stopped", false));
        _applicationPoolManager.Setup(
            s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>())
        ).Returns(false);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(
            r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())
        ).Returns("<html/>");

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await Task.Delay(4000);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(
            e => e.SendEmailAsync(
                It.Is<string>(s => s.Contains("IIS App Pool Down")),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task ExecuteAsync_NoWatchedPools_ShouldNotQueryPools()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions
        {
            PollIntervalSeconds = 1,
            WatchedApplicationPools = new List<string>()
        });

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _applicationPoolManager.Verify(
            s => s.GetApplicationPoolStatus(It.IsAny<string>()),
            Times.Never
        );
    }

    private ApplicationPoolMonitorWorker CreateWorker() => new(
        _applicationPoolManager.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        _logger.Object
    );
}
