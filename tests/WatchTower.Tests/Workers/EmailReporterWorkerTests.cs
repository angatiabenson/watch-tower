using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class EmailReporterWorkerTests
{
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IPortalSyncState> _syncState = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<DisplayOptions>> _displayOptions = new();
    private readonly Mock<IOptionsMonitor<PortalOptions>> _portalOptions = new();
    private readonly Mock<ILogger<EmailReporterWorker>> _logger = new();

    public EmailReporterWorkerTests()
    {
        // Set DailyReportTime to 2 seconds from now in configured timezone so test triggers quickly
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi");
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var targetLocal = nowLocal.AddSeconds(2);
        var reportTime = targetLocal.ToString("HH:mm:ss");

        _options.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            DailyReportTime = reportTime,
            EmergencyCooldownSeconds = 300
        });

        _displayOptions.Setup(o => o.CurrentValue).Returns(new DisplayOptions
        {
            Timezone = "Africa/Nairobi"
        });

        _portalOptions.Setup(o => o.CurrentValue).Returns(new PortalOptions { Enabled = false });
        _syncState.Setup(s => s.GetLastForwardedRowIdAsync(It.IsAny<string>())).ReturnsAsync(0L);

        // Default: report builds and the marker file from a previous run should not block.
        ResetLastReportMarker();
    }

    private static void ResetLastReportMarker()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "last-report.txt");
        if (File.Exists(path)) File.Delete(path);
    }

    private EmailReporterWorker CreateWorker() => new(
        _reportBuilder.Object,
        _emailService.Object,
        _metricsStore.Object,
        _syncState.Object,
        _options.Object,
        _displayOptions.Object,
        _portalOptions.Object,
        _logger.Object);

    [Fact]
    public async Task ExecuteAsync_ShouldSendDailyReport()
    {
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(24)
        });
        _reportBuilder.Setup(r => r.RenderScheduledReportHtml(It.IsAny<ServerReport>())).Returns("<html>report</html>");
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await Task.Delay(4000);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Daily Report")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPurgeOldData()
    {
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(24)
        });
        _reportBuilder.Setup(r => r.RenderScheduledReportHtml(It.IsAny<ServerReport>())).Returns("<html/>");
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _metricsStore.Setup(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>()))
            .Returns(new PurgeReportResult(true, true));

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await Task.Delay(4000);
        await worker.StopAsync(CancellationToken.None);

        // Portal disabled -> purge everything that was reported.
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSendFails_ShouldNotPurgeReportedData()
    {
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(24)
        });
        _reportBuilder.Setup(r => r.RenderScheduledReportHtml(It.IsAny<ServerReport>())).Returns("<html/>");
        // Send fails -> the report did not reach anyone.
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await Task.Delay(4000);
        await worker.StopAsync(CancellationToken.None);

        // Reported data must be preserved when the email never sent.
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }
}
