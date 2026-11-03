using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class EmailReporterWorkerTests : IDisposable
{
    // Isolated data dir so the last-report marker never leaks between tests or into bin/.
    private readonly WatchTowerPaths _paths = new(AppContext.BaseDirectory,
        Path.Combine(Path.GetTempPath(), "wt-tests", Guid.NewGuid().ToString("N")));

    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IPortalSyncState> _syncState = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<DisplayOptions>> _displayOptions = new();
    private readonly Mock<IOptionsMonitor<PortalOptions>> _portalOptions = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T06:00:00Z"));
    private readonly Mock<ILogger<EmailReporterWorker>> _logger = new();

    public EmailReporterWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new ReportingOptions { DailyReportTime = "08:00", EmergencyCooldownSeconds = 300 });
        _displayOptions.Setup(o => o.CurrentValue).Returns(new DisplayOptions { Timezone = "Africa/Nairobi" });
        _portalOptions.Setup(o => o.CurrentValue).Returns(new PortalOptions { Enabled = false });
        _syncState.Setup(s => s.GetLastForwardedRowIdAsync(It.IsAny<string>())).ReturnsAsync(0L);

        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport
        {
            GeneratedAt = _time.GetUtcNow().UtcDateTime,
            ReportPeriod = TimeSpan.FromHours(24)
        });
        _reportBuilder.Setup(r => r.RenderScheduledReportHtml(It.IsAny<ServerReport>())).Returns("<html/>");
        _metricsStore.Setup(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>()))
            .Returns(new PurgeReportResult(true, true));
    }

    [Fact]
    public async Task RunDailyReport_OnSuccess_SendsThenPurges()
    {
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await CreateWorker().RunDailyReportAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Daily Report")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task RunDailyReport_WhenSendFails_DoesNotPurgeReportedData()
    {
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await CreateWorker().RunDailyReportAsync(CancellationToken.None);

        // Reported data must be preserved when the email never sent (only the age-based backstop runs).
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        _metricsStore.Verify(s => s.Purge(It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task RunDailyReport_WhenPortalEnabled_PurgesUpToSentCursor()
    {
        _portalOptions.Setup(o => o.CurrentValue).Returns(new PortalOptions { Enabled = true });
        _syncState.Setup(s => s.GetLastForwardedRowIdAsync("cpu_sent")).ReturnsAsync(42L);
        _syncState.Setup(s => s.GetLastForwardedRowIdAsync("ram_sent")).ReturnsAsync(7L);
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await CreateWorker().RunDailyReportAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.PurgeReportedData(42L, 7L), Times.Once);
    }

    private EmailReporterWorker CreateWorker() => new(
        _reportBuilder.Object,
        _emailService.Object,
        _metricsStore.Object,
        _syncState.Object,
        _options.Object,
        _displayOptions.Object,
        _portalOptions.Object,
        _time,
        _paths,
        _logger.Object);

    public void Dispose()
    {
        try { Directory.Delete(_paths.DataDirectory, recursive: true); } catch (IOException) { }
    }
}