using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class ApplicationPoolMonitorWorkerTests
{
    private readonly Mock<IIisApplicationPoolManager> _applicationPoolManager = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<ApplicationPoolMonitorOptions>> _options = new();
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
        _emailService.Setup(e => e.IsConfigured).Returns(true);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
        _reportBuilder.Setup(r => r.RenderAlertHtml(It.IsAny<AlertNotification>())).Returns("<html/>");
    }

    private static ApplicationPoolStatus Pool(string state, bool running) =>
        new(DateTime.UtcNow, "DefaultAppPool", state, running);

    private void PoolStatusIs(ApplicationPoolStatus status) =>
        _applicationPoolManager
            .Setup(s => s.GetApplicationPoolStatusAsync("DefaultAppPool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

    private void PoolStatusSequence(params ApplicationPoolStatus[] statuses)
    {
        var sequence = _applicationPoolManager
            .SetupSequence(s => s.GetApplicationPoolStatusAsync("DefaultAppPool", It.IsAny<CancellationToken>()));
        foreach (var status in statuses)
            sequence = sequence.ReturnsAsync(status);
    }

    private void StartReturns(bool result) =>
        _applicationPoolManager
            .Setup(s => s.StartApplicationPoolAsync("DefaultAppPool", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void VerifyStart(Times times) =>
        _applicationPoolManager.Verify(
            s => s.StartApplicationPoolAsync("DefaultAppPool", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task PollOnce_StartedPool_ShouldStoreStatus()
    {
        PoolStatusIs(Pool("Started", true));

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddApplicationPoolStatus(It.Is<ApplicationPoolStatus>(st => st.IsRunning)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PollOnce_StoppedPool_ShouldAttemptStart()
    {
        PoolStatusSequence(Pool("Stopped", false), Pool("Started", true));
        StartReturns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        VerifyStart(Times.Once());
    }

    [Fact]
    public async Task PollOnce_SuccessfulStart_ShouldLogIncident()
    {
        PoolStatusSequence(Pool("Stopped", false), Pool("Started", true));
        StartReturns(true);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.ApplicationPoolStarted && i.Description.Contains("DefaultAppPool"))), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RepeatedFailedStart_ShouldSendEmergencyEmail()
    {
        PoolStatusIs(Pool("Stopped", false));
        StartReturns(false);

        var worker = CreateWorker();
        // Escalation fires once consecutive failures reach MaxStartAttempts (3).
        for (var i = 0; i < 3; i++)
            await worker.PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("IIS App Pool Down")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_RepeatedFailedStartWithFailingEmail_RecordsOneEmergencyWhileOpen()
    {
        PoolStatusIs(Pool("Stopped", false));
        StartReturns(false);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var worker = CreateWorker();
        for (var i = 0; i < 6; i++)
            await worker.PollOnceAsync(CancellationToken.None);

        // Attempts 3..6 are all past MaxStartAttempts, but the alert opened at attempt 3.
        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.ApplicationPoolStartFailed)), Times.Once);
        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_PoolRunningAfterAlert_SendsResolvedWithStartedIncident()
    {
        PoolStatusIs(Pool("Stopped", false));
        StartReturns(false);
        var worker = CreateWorker();
        for (var i = 0; i < 3; i++)
            await worker.PollOnceAsync(CancellationToken.None);

        // Next attempt works: the started incident resolves the alert.
        PoolStatusSequence(Pool("Stopped", false), Pool("Started", true));
        StartReturns(true);
        await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.ApplicationPoolStarted)), Times.Once);
        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Resolved: IIS App Pool Down")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_UnknownState_IsNotTreatedAsDown()
    {
        // A query timeout/error yields Unknown: record it, but no incident and no start attempt.
        PoolStatusIs(Pool(ApplicationPoolStatus.UnknownStatus, false));

        var worker = CreateWorker();
        for (var i = 0; i < 4; i++)
            await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddApplicationPoolStatus(It.Is<ApplicationPoolStatus>(st => st.Status == "Unknown")), Times.Exactly(4));
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);
        VerifyStart(Times.Never());
    }

    [Fact]
    public async Task PollOnce_UnknownRecheckAfterStart_IsNotCountedAsFailure()
    {
        PoolStatusSequence(
            Pool("Stopped", false), Pool(ApplicationPoolStatus.UnknownStatus, false),
            Pool("Stopped", false), Pool(ApplicationPoolStatus.UnknownStatus, false),
            Pool("Stopped", false), Pool(ApplicationPoolStatus.UnknownStatus, false));
        StartReturns(true);

        var worker = CreateWorker();
        for (var i = 0; i < 3; i++)
            await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.ApplicationPoolStartFailed)), Times.Never);
        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.ApplicationPoolStarted)), Times.Never);
    }

    [Fact]
    public async Task PollOnce_PassesCancellationTokenToManager()
    {
        using var cts = new CancellationTokenSource();
        PoolStatusIs(Pool("Started", true));

        await CreateWorker().PollOnceAsync(cts.Token);

        _applicationPoolManager.Verify(s => s.GetApplicationPoolStatusAsync("DefaultAppPool", cts.Token), Times.Once);
    }

    [Fact]
    public async Task PollOnce_NoWatchedPools_ShouldNotQueryPools()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions { WatchedApplicationPools = new List<string>() });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _applicationPoolManager.Verify(
            s => s.GetApplicationPoolStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ApplicationPoolMonitorWorker CreateWorker() => new(
        _applicationPoolManager.Object,
        _metricsStore.Object,
        TestAlerts.WithEmail(_metricsStore.Object, TimeProvider.System, _emailService.Object, _reportBuilder.Object),
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        TimeProvider.System,
        _logger.Object
    );
}