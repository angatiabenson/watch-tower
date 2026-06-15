using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly Mock<ILogger<RamMonitorWorker>> _logger = new();

    public RamMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new RamMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 90,
            SustainedSeconds = 2,
            TopProcessCount = 5
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStoreRamMetrics()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(DateTime.UtcNow, 50, 16000, 32000));

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddRamMetric(It.IsAny<RamMetric>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_BelowThreshold_ShouldNotSendEmail()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(DateTime.UtcNow, 50, 16000, 32000));

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AboveThreshold_ShouldLogSpikeIncident()
    {
        _metricsProvider.Setup(m => m.GetRamMetric()).Returns(new RamMetric(DateTime.UtcNow, 95, 1600, 32000));
        _metricsProvider.Setup(m => m.GetTopMemoryProcessesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProcessInfo>
        {
            new("sqlservr", 1234, 500, 4096)
        });

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.RamSpikeDetected &&
            i.TopProcesses != null && i.TopProcesses.Count > 0)), Times.Once);
    }

    private RamMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        _logger.Object);
}
