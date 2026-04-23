using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly Mock<ILogger<CpuMonitorWorker>> _logger = new();

    public CpuMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new CpuMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 99,
            SustainedSeconds = 2,
            TopProcessCount = 3
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStoreCpuMetrics()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(50.0f);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddCpuMetric(It.IsAny<CpuMetric>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_BelowThreshold_ShouldNotSendEmail()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(50.0f);

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
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        _metricsProvider.Setup(m => m.GetTopCpuProcesses(It.IsAny<int>())).Returns(new List<ProcessInfo>
        {
            new("sqlservr", 1234, 500, 2048)
        });

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(i =>
            i.Type == IncidentType.CpuSpikeDetected &&
            i.TopProcesses != null && i.TopProcesses.Count > 0)), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AboveThreshold_ShouldUseConfiguredTopProcessCount()
    {
        _options.Setup(o => o.CurrentValue).Returns(new CpuMonitorOptions
        {
            PollIntervalSeconds = 1,
            ThresholdPercent = 99,
            SustainedSeconds = 30,
            TopProcessCount = 5
        });
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        _metricsProvider.Setup(m => m.GetTopCpuProcesses(It.IsAny<int>())).Returns(new List<ProcessInfo>());

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsProvider.Verify(m => m.GetTopCpuProcesses(5), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SustainedBreach_ShouldSendEmergencyEmail()
    {
        _metricsProvider.Setup(m => m.GetCpuUsage()).Returns(99.5f);
        _metricsProvider.Setup(m => m.GetTopCpuProcesses(It.IsAny<int>())).Returns(new List<ProcessInfo>());
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await Task.Delay(4000);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("CPU Emergency")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private CpuMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        _options.Object,
        _reportingOptions.Object,
        _logger.Object);
}
