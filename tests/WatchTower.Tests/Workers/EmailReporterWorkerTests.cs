using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class EmailReporterWorkerTests
{
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _options = new();
    private readonly Mock<ILogger<EmailReporterWorker>> _logger = new();

    public EmailReporterWorkerTests()
    {
        // Set DailyReportTime to 1 second from now in EAT so test triggers quickly
        var eatZone = TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");
        var nowEat = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eatZone);
        var targetEat = nowEat.AddSeconds(2);
        var reportTime = targetEat.ToString("HH:mm:ss");

        _options.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            DailyReportTime = reportTime,
            EmergencyCooldownSeconds = 300
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendDailyReport()
    {
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(24)
        });
        _reportBuilder.Setup(r => r.RenderScheduledReportHtml(It.IsAny<ServerReport>())).Returns("<html>report</html>");

        var worker = new EmailReporterWorker(_reportBuilder.Object, _emailService.Object, _metricsStore.Object, _options.Object, _logger.Object);
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

        var worker = new EmailReporterWorker(_reportBuilder.Object, _emailService.Object, _metricsStore.Object, _options.Object, _logger.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await Task.Delay(4000);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.Purge(It.Is<TimeSpan>(t => t.TotalHours == 25)), Times.AtLeastOnce);
    }
}
