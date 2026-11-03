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

public class CpuMonitorWorkerTests
{
    private readonly Mock<ISystemMetricsProvider> _metricsProvider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<CpuMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
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
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _metricsProvider.Setup(m => m.GetTopCpuProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessInfo>());
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
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

    [Fact]
    public async Task PollOnce_FailedEmergencySend_DoesNotStartCooldown()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // send keeps failing

        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(301));
        await worker.PollOnceAsync(CancellationToken.None); // emergency #1 (fails)
        _time.Advance(TimeSpan.FromSeconds(301));
        await worker.PollOnceAsync(CancellationToken.None); // emergency #2 (cooldown not started -> retried)

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("CPU Emergency")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
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
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        _time,
        _logger.Object);
}