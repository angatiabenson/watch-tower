using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class RamMonitorWorkerTests
{
    private readonly Mock<ISystemMetricsProvider> _metricsProvider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<RamMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<ILogger<RamMonitorWorker>> _logger = new();

    public RamMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new RamMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 90,
            SustainedSeconds = 300,
            TopProcessCount = 5
        });
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _metricsProvider.Setup(m => m.GetTopMemoryProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessInfo>());
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
    }

    [Fact]
    public async Task PollOnce_ShouldStoreRamMetric()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(_time.GetUtcNow().UtcDateTime, 50, 16000, 32000));

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddRamMetric(It.Is<RamMetric>(m => m.UsagePercent == 50)), Times.Once);
    }

    [Fact]
    public async Task PollOnce_BelowThreshold_ShouldNotSendEmail()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(_time.GetUtcNow().UtcDateTime, 50, 16000, 32000));

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_AboveThreshold_ShouldLogSpikeIncident()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(_time.GetUtcNow().UtcDateTime, 95, 1600, 32000));
        _metricsProvider.Setup(m => m.GetTopMemoryProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessInfo> { new("sqlservr", 1234, 500, 4096) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.RamSpikeDetected && i.TopProcesses != null && i.TopProcesses.Count > 0)), Times.Once);
    }

    [Fact]
    public async Task PollOnce_SustainedBreach_ShouldSendEmergencyEmail()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(_time.GetUtcNow().UtcDateTime, 95, 1600, 32000));
        var worker = CreateWorker();

        await worker.PollOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(301));
        await worker.PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("RAM Emergency")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private RamMonitorWorker CreateWorker() => new(
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
