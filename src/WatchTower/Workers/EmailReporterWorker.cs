using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class EmailReporterWorker : BackgroundService
{
    private readonly IReportBuilder _reportBuilder;
    private readonly IEmailService _emailService;
    private readonly IMetricsStore _metricsStore;
    private readonly IOptionsMonitor<ReportingOptions> _options;
    private readonly IOptionsMonitor<DisplayOptions> _displayOptions;
    private readonly ILogger<EmailReporterWorker> _logger;

    public EmailReporterWorker(
        IReportBuilder reportBuilder,
        IEmailService emailService,
        IMetricsStore metricsStore,
        IOptionsMonitor<ReportingOptions> options,
        IOptionsMonitor<DisplayOptions> displayOptions,
        ILogger<EmailReporterWorker> logger
    )
    {
        _reportBuilder = reportBuilder;
        _emailService = emailService;
        _metricsStore = metricsStore;
        _options = options;
        _displayOptions = displayOptions;
        _logger = logger;
    }

    private TimeZoneInfo GetConfiguredZone()
    {
        var tzId = _displayOptions.CurrentValue.Timezone;
        try { return TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email Reporter Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextReport();
                _logger.LogInformation(
                    "Next daily report scheduled in {Hours:F1} hours",
                    delay.TotalHours
                );

                await Task.Delay(delay, stoppingToken);

                _logger.LogInformation("Generating daily scheduled report");

                var reportPeriod = TimeSpan.FromHours(24);

                var report = _reportBuilder.BuildReport(reportPeriod);
                var zone = GetConfiguredZone();
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(report.GeneratedAt, zone);
                var html = _reportBuilder.RenderScheduledReportHtml(report);
                await _emailService.SendEmailAsync(
                    $"[WatchTower] Daily Report - {localTime:yyyy-MM-dd HH:mm}",
                    html,
                    stoppingToken
                );

                // Purge all data after daily report sent successfully
                _metricsStore.Purge(TimeSpan.Zero);
                _logger.LogInformation("Metrics data purged after daily report");

                // Guard: wait 60s so the next GetDelayUntilNextReport() call
                // is safely past the target time and schedules for tomorrow
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
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

        var zone = GetConfiguredZone();
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);

        var nextReportLocal = nowLocal.Date + reportTimeOfDay;
        if (nextReportLocal <= nowLocal)
            nextReportLocal = nextReportLocal.AddDays(1);

        var nextReportUtc = TimeZoneInfo.ConvertTimeToUtc(nextReportLocal, zone);
        return nextReportUtc - nowUtc;
    }
}
