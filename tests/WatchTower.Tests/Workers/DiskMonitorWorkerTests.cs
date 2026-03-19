using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class DiskMonitorWorkerTests
{
    private readonly Mock<ISystemMetricsProvider> _metricsProvider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<DiskMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
    private readonly Mock<ILogger<DiskMonitorWorker>> _logger = new();

    public DiskMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new DiskMonitorOptions
        {
            PollIntervalSeconds = 1,
            WarningThresholdPercent = 70,
            CriticalThresholdPercent = 85,
            QueueLengthWarningThreshold = 2.0
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });

        // Default: return empty disk queue metrics
        _metricsProvider.Setup(m => m.GetDiskQueueMetrics()).Returns(new List<DiskQueueMetric>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStoreDiskMetrics()
    {
        var disks = new List<DiskMetric>
        {
            new(DateTime.UtcNow, "C:\\", 500, 300, 40)
        };
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(disks);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddDiskMetric(It.IsAny<DiskMetric>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_CriticalDisk_ShouldSendEmail()
    {
        var disks = new List<DiskMetric>
        {
            new(DateTime.UtcNow, "C:\\", 500, 50, 90)
        };
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(disks);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Disk Critical")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_BelowWarning_ShouldNotSendEmail()
    {
        var disks = new List<DiskMetric>
        {
            new(DateTime.UtcNow, "C:\\", 500, 300, 40)
        };
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(disks);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_HighDiskQueue_ShouldSendEmail()
    {
        var disks = new List<DiskMetric> { new(DateTime.UtcNow, "C:\\", 500, 300, 40) };
        var queues = new List<DiskQueueMetric> { new(DateTime.UtcNow, "0 C:", 5.0) };

        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(disks);
        _metricsProvider.Setup(m => m.GetDiskQueueMetrics()).Returns(queues);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Disk Queue High")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private DiskMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        _options.Object,
        _reportingOptions.Object,
        _logger.Object);
}
