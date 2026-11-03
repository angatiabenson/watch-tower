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

public class DiskMonitorWorkerTests
{
    private readonly Mock<ISystemMetricsProvider> _metricsProvider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<DiskMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
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
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _metricsProvider.Setup(m => m.GetDiskQueueMetrics()).Returns(new List<DiskQueueMetric>());
        _emailService.Setup(e => e.IsConfigured).Returns(true);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
    }

    [Fact]
    public async Task PollOnce_ShouldStoreDiskMetrics()
    {
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(new List<DiskMetric> { new(_time.GetUtcNow().UtcDateTime, "C:\\", 500, 300, 40) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddDiskMetric(It.IsAny<DiskMetric>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_CriticalDisk_ShouldSendEmail()
    {
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(new List<DiskMetric> { new(_time.GetUtcNow().UtcDateTime, "C:\\", 500, 50, 90) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Disk Critical")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_BelowWarning_ShouldNotSendEmail()
    {
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(new List<DiskMetric> { new(_time.GetUtcNow().UtcDateTime, "C:\\", 500, 300, 40) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_HighDiskQueue_ShouldSendEmail()
    {
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(new List<DiskMetric> { new(_time.GetUtcNow().UtcDateTime, "C:\\", 500, 300, 40) });
        _metricsProvider.Setup(m => m.GetDiskQueueMetrics()).Returns(new List<DiskQueueMetric> { new(_time.GetUtcNow().UtcDateTime, "0 C:", 5.0) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Disk Queue High")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_DiskQueueProviderThrows_StillRecordsDiskUsage()
    {
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(new List<DiskMetric> { new(_time.GetUtcNow().UtcDateTime, "C:\\", 500, 300, 40) });
        _metricsProvider.Setup(m => m.GetDiskQueueMetrics()).Throws(new InvalidOperationException("counters unavailable"));

        var act = () => CreateWorker().PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _metricsStore.Verify(s => s.AddDiskMetric(It.IsAny<DiskMetric>()), Times.Once);
    }

    private void CriticalDrives(params string[] drives) =>
        _metricsProvider.Setup(m => m.GetDiskMetrics()).Returns(() => drives
            .Select(d => new DiskMetric(_time.GetUtcNow().UtcDateTime, d, 500, 50, 90))
            .ToList());

    [Fact]
    public async Task PollOnce_CriticalDiskWithFailingEmail_RecordsOneIncidentPerCooldown()
    {
        CriticalDrives("C:\\");
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var worker = CreateWorker();

        // Previously each of these polls recorded a new incident, published a portal event and
        // rebuilt the 24h report because the cooldown only started on a delivered email.
        for (var i = 0; i < 5; i++)
        {
            await worker.PollOnceAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(60));
        }

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.DiskCritical)), Times.Once);
        _reportBuilder.Verify(r => r.BuildReport(It.IsAny<TimeSpan>()), Times.Once);

        _time.Advance(TimeSpan.FromSeconds(300));
        await worker.PollOnceAsync(CancellationToken.None);
        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.DiskCritical)), Times.Exactly(2));
    }

    [Fact]
    public async Task PollOnce_CriticalDiskWithSmtpUnconfigured_SkipsEmailButRecordsIncident()
    {
        CriticalDrives("C:\\");
        _emailService.Setup(e => e.IsConfigured).Returns(false);
        var worker = CreateWorker();

        await worker.PollOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(60));
        await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Type == IncidentType.DiskCritical)), Times.Once);
        _reportBuilder.Verify(r => r.BuildReport(It.IsAny<TimeSpan>()), Times.Never);
        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_CooldownIsPerDrive()
    {
        CriticalDrives("C:\\");
        var worker = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);

        // A second drive going critical inside C:'s cooldown still raises its own incident.
        CriticalDrives("C:\\", "D:\\");
        _time.Advance(TimeSpan.FromSeconds(60));
        await worker.PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Description.Contains("C:\\"))), Times.Once);
        _metricsStore.Verify(s => s.AddIncident(It.Is<Incident>(x => x.Description.Contains("D:\\"))), Times.Once);
    }

    private DiskMonitorWorker CreateWorker() => new(
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