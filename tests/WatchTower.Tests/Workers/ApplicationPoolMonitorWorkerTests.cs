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
        // Zero intra-iteration delays so a single PollOnceAsync completes immediately.
        _options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions
        {
            PollIntervalSeconds = 1,
            MaxStartAttempts = 3,
            StartDelaySeconds = 0,
            StartTimeoutSeconds = 1,
            StableStateDelaySeconds = 0,
            WatchedApplicationPools = new List<string> { "DefaultAppPool" }
        });
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
    }

    private static ApplicationPoolStatus Pool(string state, bool running) =>
        new(DateTime.UtcNow, "DefaultAppPool", state, running);

    [Fact]
    public async Task PollOnce_StartedPool_ShouldStoreStatus()
    {
        _applicationPoolManager.Setup(s => s.GetApplicationPoolStatus("DefaultAppPool")).Returns(Pool("Started", true));

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddApplicationPoolStatus(It.Is<ApplicationPoolStatus>(st => st.IsRunning)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollOnce_StoppedPool_ShouldAttemptStart()
    {
        _applicationPoolManager.SetupSequence(s => s.GetApplicationPoolStatus("DefaultAppPool"))
            .Returns(Pool("Stopped", false))
            .Returns(Pool("Started", true));
        _applicationPoolManager.Setup(s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>())).Returns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _applicationPoolManager.Verify(s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_SuccessfulStart_ShouldLogIncident()
    {
        _applicationPoolManager.SetupSequence(s => s.GetApplicationPoolStatus("DefaultAppPool"))
            .Returns(Pool("Stopped", false))
            .Returns(Pool("Started", true));
        _applicationPoolManager.Setup(s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>())).Returns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ApplicationPoolStarted && i.Description.Contains("DefaultAppPool"))), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RepeatedFailedStart_ShouldSendEmergencyEmail()
    {
        _applicationPoolManager.Setup(s => s.GetApplicationPoolStatus("DefaultAppPool")).Returns(Pool("Stopped", false));
        _applicationPoolManager.Setup(s => s.StartApplicationPool("DefaultAppPool", It.IsAny<TimeSpan>())).Returns(false);

        var worker = CreateWorker();
        // Escalation fires once consecutive failures reach MaxStartAttempts (3).
        for (var i = 0; i < 3; i++)
            await worker.PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("IIS App Pool Down")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_NoWatchedPools_ShouldNotQueryPools()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions { WatchedApplicationPools = new List<string>() });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _applicationPoolManager.Verify(s => s.GetApplicationPoolStatus(It.IsAny<string>()), Times.Never);
    }

    private ApplicationPoolMonitorWorker CreateWorker() => new(
        _applicationPoolManager.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        TimeProvider.System,
        _logger.Object
    );
}