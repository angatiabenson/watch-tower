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
            CriticalThresholdPercent = 85
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });
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

    private DiskMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        _options.Object,
        _reportingOptions.Object,
        _logger.Object);
}
