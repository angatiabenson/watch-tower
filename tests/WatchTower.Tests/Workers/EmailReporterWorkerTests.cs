using AwesomeAssertions;

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
        _metricsStore.Setup(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>()))
            .Returns(new PurgeReportResult(true, true));
        _emailService.Setup(e => e.IsConfigured).Returns(true);
    }

    private void SendSucceeds(params bool[] outcomes)
    {
        var sequence = _emailService.SetupSequence(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        foreach (var outcome in outcomes)
            sequence = sequence.ReturnsAsync(outcome);
    }

    private (EmailReporterWorker Worker, List<TimeSpan> Delays) CreateWorkerRecordingRetries()
    {
        var delays = new List<TimeSpan>();
        var worker = CreateWorker();
        worker.RetryDelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        return (worker, delays);
    }

    [Fact]
    public async Task RunWithRetries_SendFailure_RetriesWithBackoffUntilSent()
    {
        SendSucceeds(false, false, true);
        var (worker, delays) = CreateWorkerRecordingRetries();

        await worker.RunDailyReportWithRetriesAsync(CancellationToken.None);

        // Previously a failed send meant waiting a full day for the next report.
        delays.Should().Equal(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunWithRetries_PersistentFailure_GivesUpAfterFourRetries()
    {
        SendSucceeds(false, false, false, false, false, false);
        var (worker, delays) = CreateWorkerRecordingRetries();

        await worker.RunDailyReportWithRetriesAsync(CancellationToken.None);

        delays.Should().Equal(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60));
        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task RunWithRetries_SmtpNotConfigured_NeitherBuildsNorRetries()
    {
        _emailService.Setup(e => e.IsConfigured).Returns(false);
        var (worker, delays) = CreateWorkerRecordingRetries();

        await worker.RunDailyReportWithRetriesAsync(CancellationToken.None);

        delays.Should().BeEmpty();
        _reportBuilder.Verify(r => r.BuildReport(It.IsAny<TimeSpan>()), Times.Never);
        _metricsStore.Verify(s => s.Purge(It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task RunDailyReport_PurgesIncidentsOnlyUpToTheReportSnapshot()
    {
        var snapshot = _time.GetUtcNow().UtcDateTime.AddMinutes(-3);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport { GeneratedAt = snapshot });
        SendSucceeds(true);

        await CreateWorker().RunDailyReportAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>(), snapshot), Times.Once);
    }

    [Fact]
    public async Task RunDailyReport_OnSuccess_SendsThenPurges()
    {
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await CreateWorker().RunDailyReportAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Daily Report")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunDailyReport_WhenSendFails_DoesNotPurgeReportedData()
    {
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await CreateWorker().RunDailyReportAsync(CancellationToken.None);

        // Reported data must be preserved when the email never sent (only the age-based backstop runs).
        _metricsStore.Verify(s => s.PurgeReportedData(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>()), Times.Never);
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

        _metricsStore.Verify(s => s.PurgeReportedData(42L, 7L, It.IsAny<DateTime>()), Times.Once);
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