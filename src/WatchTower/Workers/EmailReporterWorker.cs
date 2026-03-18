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
                var opts = _options.CurrentValue;
                var interval = TimeSpan.FromSeconds(opts.ReportIntervalSeconds);

                await Task.Delay(interval, stoppingToken);

                _logger.LogInformation("Generating scheduled report");

                // Purge old data (keep 2.5 hours)
                _metricsStore.Purge(TimeSpan.FromHours(2.5));

                var report = _reportBuilder.BuildReport(interval);
                var html = _reportBuilder.RenderScheduledReportHtml(report);
                await _emailService.SendEmailAsync(
                    $"[WatchTower] Scheduled Report - {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC",
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
}
