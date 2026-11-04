using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class CpuMonitorWorkerTests
{
    private readonly Mock<ISystemMetricsProvider> _metricsProvider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<CpuMonitorOptions>> _options = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<ILogger<CpuMonitorWorker>> _logger = new();

    public CpuMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new CpuMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 99,
            SustainedSeconds = 300,
            TopProcessCount = 3
        });
        _emailService.Setup(e => e.IsConfigured).Returns(true);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _metricsProvider.Setup(m => m.GetTopCpuProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessInfo>());
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
        _reportBuilder.Setup(r => r.RenderAlertHtml(It.IsAny<AlertNotification>())).Returns("<html/>");
    }

    [Fact]
    public async Task PollOnce_ShouldStoreCpuMetric()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(50.0f);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddCpuMetric(It.Is<CpuMetric>(m => m.UsagePercent == 50.0)), Times.Once);
    }

    [Fact]
    public async Task PollOnce_BelowThreshold_ShouldNotSendEmail()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(50.0f);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_AboveThreshold_ShouldLogSpikeIncidentOnce()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        _metricsProvider.Setup(m => m.GetTopCpuProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessInfo> { new("sqlservr", 1234, 500, 2048) });

        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);
        await worker.PollOnceAsync(CancellationToken.None); // still breaching, no new spike

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.CpuSpikeDetected && i.TopProcesses != null && i.TopProcesses.Count > 0)), Times.Once);
    }

    [Fact]
    public async Task PollOnce_AboveThreshold_ShouldUseConfiguredTopProcessCount()
    {
        _options.Setup(o => o.CurrentValue).Returns(new CpuMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 99,
            SustainedSeconds = 300,
            TopProcessCount = 5
        });
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsProvider.Verify(m => m.GetTopCpuProcessesAsync(5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_SustainedBreach_ShouldSendEmergencyEmail()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        var worker = CreateWorker();

        // First breach starts the sustained window; advancing fake time past SustainedSeconds
        // makes the next poll cross the threshold deterministically (no real waiting).
        await worker.PollOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(301));
        await worker.PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("CPU Emergency")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void UseImmediateEmergency() =>
        // SustainedSeconds = 0: every breaching poll raises the alert, so only the alert manager
        // decides whether anything new is recorded or sent.
        _options.Setup(o => o.CurrentValue).Returns(new CpuMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 99,
            SustainedSeconds = 0,
            TopProcessCount = 3
        });

    [Fact]
    public async Task PollOnce_FailedEmergencySend_IsNotRetriedEveryPoll()
    {
        UseImmediateEmergency();
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // SMTP keeps failing

        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None); // alert opens (send fails)
        _time.Advance(TimeSpan.FromSeconds(10));
        await worker.PollOnceAsync(CancellationToken.None); // still open: nothing new

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.CpuThresholdBreached)), Times.Once);
        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("CPU Emergency")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Alert emails are short by default: the 24 h report is not built.
        _reportBuilder.Verify(r => r.BuildReport(It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_StillBreachingAfterRenotifyInterval_SendsReminderWithoutNewIncident()
    {
        UseImmediateEmergency();
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);

        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        await worker.PollOnceAsync(CancellationToken.None); // before the reminder interval
        _time.Advance(TimeSpan.FromMinutes(31));
        await worker.PollOnceAsync(CancellationToken.None); // 61 min after the first notification

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.CpuThresholdBreached)), Times.Once);
        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Still open: CPU Emergency")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollOnce_ClearForRecoveryWindow_SendsResolvedOnce()
    {
        UseImmediateEmergency();
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);

        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(20f);
        await worker.PollOnceAsync(CancellationToken.None); // clear window starts
        _time.Advance(TimeSpan.FromSeconds(299));
        await worker.PollOnceAsync(CancellationToken.None); // not yet
        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Resolved")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        _time.Advance(TimeSpan.FromSeconds(1));
        await worker.PollOnceAsync(CancellationToken.None);
        await worker.PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Resolved: CPU Emergency")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollOnce_BriefDipBetweenBreaches_DoesNotResolve()
    {
        UseImmediateEmergency();
        _metricsProvider.SetupSequence(m => m.GetCpuUsage()).Returns(99.5f).Returns(20f).Returns(99.5f).Returns(20f);
        var worker = CreateWorker();

        await worker.PollOnceAsync(CancellationToken.None); // opens
        await worker.PollOnceAsync(CancellationToken.None); // dip: clear window starts
        _time.Advance(TimeSpan.FromSeconds(200));
        await worker.PollOnceAsync(CancellationToken.None); // breach again: window reset
        _time.Advance(TimeSpan.FromSeconds(200));
        await worker.PollOnceAsync(CancellationToken.None); // clear for 0 s only

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Resolved")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_SmtpNotConfigured_RecordsIncidentWithoutBuildingEmail()
    {
        UseImmediateEmergency();
        _emailService.Setup(e => e.IsConfigured).Returns(false);
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);

        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(10));
        await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.CpuThresholdBreached)), Times.Once);
        _reportBuilder.Verify(r => r.BuildReport(It.IsAny<TimeSpan>()), Times.Never);
        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_CpuUnavailable_SkipsCycleWithoutRecording()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(float.NaN);

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddCpuMetric(It.IsAny<CpuMetric>()), Times.Never);
        _metricsStore.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_CpuProviderThrows_SkipsCycleInsteadOfFailing()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Throws(new InvalidOperationException("counter registry corrupt"));

        var act = () => CreateWorker().PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _metricsStore.Verify(s => s.AddCpuMetric(It.IsAny<CpuMetric>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_CpuRecoversAfterOutage_RecordsAgain()
    {
        _metricsProvider.SetupSequence(m => m.GetCpuUsage()).Returns(float.NaN).Returns(10f);
        var worker = CreateWorker();

        await worker.PollOnceAsync(CancellationToken.None);
        await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddCpuMetric(It.Is<CpuMetric>(m => m.UsagePercent == 10)), Times.Once);
    }

    private CpuMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        TestAlerts.WithEmail(_metricsStore.Object, _time, _emailService.Object, _reportBuilder.Object),
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _time,
        _logger.Object);
}