using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class EmailReporterWorker : BackgroundService
{
    private static readonly TimeZoneInfo EatZone = TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");

    private readonly IReportBuilder _reportBuilder;
    private readonly IEmailService _emailService;
    private readonly IMetricsStore _metricsStore;
    private readonly IOptionsMonitor<ReportingOptions> _options;
    private readonly ILogger<EmailReporterWorker> _logger;

    public EmailReporterWorker(
        IReportBuilder reportBuilder,
        IEmailService emailService,
        IMetricsStore metricsStore,
        IOptionsMonitor<ReportingOptions> options,
        ILogger<EmailReporterWorker> logger)
    {
        _reportBuilder = reportBuilder;
        _emailService = emailService;
        _metricsStore = metricsStore;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email Reporter Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextReport();
                _logger.LogInformation("Next daily report scheduled in {Hours:F1} hours", delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                _logger.LogInformation("Generating daily scheduled report");

                var reportPeriod = TimeSpan.FromHours(24);

                // Purge data older than 25 hours (keep buffer beyond 24h period)
                _metricsStore.Purge(TimeSpan.FromHours(25));

                var report = _reportBuilder.BuildReport(reportPeriod);
                var eatTime = TimeZoneInfo.ConvertTimeFromUtc(report.GeneratedAt, EatZone);
                var html = _reportBuilder.RenderScheduledReportHtml(report);
                await _emailService.SendEmailAsync(
                    $"[WatchTower] Daily Report - {eatTime:yyyy-MM-dd HH:mm} EAT",
                    html,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Email Reporter Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Email Reporter Worker stopped");
    }

    private TimeSpan GetDelayUntilNextReport()
    {
        var opts = _options.CurrentValue;

        if (!TimeSpan.TryParse(opts.DailyReportTime, out var reportTimeOfDay))
            reportTimeOfDay = TimeSpan.FromHours(8);

        var nowUtc = DateTime.UtcNow;
        var nowEat = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, EatZone);

        var nextReportEat = nowEat.Date + reportTimeOfDay;
        if (nextReportEat <= nowEat)
            nextReportEat = nextReportEat.AddDays(1);

        var nextReportUtc = TimeZoneInfo.ConvertTimeToUtc(nextReportEat, EatZone);
        return nextReportUtc - nowUtc;
    }
}
