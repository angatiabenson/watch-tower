using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class EmailReporterWorker : BackgroundService
{
    private readonly IReportBuilder _reportBuilder;
    private readonly IEmailService _emailService;
    private readonly IMetricsStore _metricsStore;
    private readonly IPortalSyncState _syncState;
    private readonly IOptionsMonitor<ReportingOptions> _options;
    private readonly IOptionsMonitor<DisplayOptions> _displayOptions;
    private readonly IOptionsMonitor<PortalOptions> _portalOptions;
    private readonly ILogger<EmailReporterWorker> _logger;

    // Persisted marker (survives restarts and purges) recording the local date of the
    // last successfully-sent daily report, so a restart after the scheduled time does
    // not silently skip that day's report.
    private readonly string _lastReportMarkerPath =
        Path.Combine(AppContext.BaseDirectory, "last-report.txt");

    // Backstop retention used only when the daily report repeatedly fails to send, so the
    // metric tables stay bounded without losing the most recent data.
    private const int SafetyRetentionHours = 48;

    public EmailReporterWorker(
        IReportBuilder reportBuilder,
        IEmailService emailService,
        IMetricsStore metricsStore,
        IPortalSyncState syncState,
        IOptionsMonitor<ReportingOptions> options,
        IOptionsMonitor<DisplayOptions> displayOptions,
        IOptionsMonitor<PortalOptions> portalOptions,
        ILogger<EmailReporterWorker> logger
    )
    {
        _reportBuilder = reportBuilder;
        _emailService = emailService;
        _metricsStore = metricsStore;
        _syncState = syncState;
        _options = options;
        _displayOptions = displayOptions;
        _portalOptions = portalOptions;
        _logger = logger;
    }

    private TimeZoneInfo GetConfiguredZone()
    {
        var tzId = _displayOptions.CurrentValue.Timezone;
        try { return TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timezone '{Tz}' could not be resolved; falling back to E. Africa Standard Time", tzId);
            return TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email Reporter Worker started");

        // Catch-up: if today's scheduled time has already passed and we have not yet
        // reported today, run it now rather than waiting until tomorrow.
        try
        {
            if (IsReportOwed())
            {
                _logger.LogInformation("Daily report is owed for today; running catch-up report");
                await RunDailyReportAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running catch-up daily report");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextReport();
                _logger.LogInformation("Next daily report scheduled in {Hours:F1} hours", delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                await RunDailyReportAsync(stoppingToken);
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

    private async Task RunDailyReportAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Generating daily scheduled report");

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var zone = GetConfiguredZone();
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(report.GeneratedAt, zone);
        var html = _reportBuilder.RenderScheduledReportHtml(report);

        var sent = await _emailService.SendEmailAsync(
            $"[WatchTower] Daily Report - {localTime:yyyy-MM-dd HH:mm}",
            html,
            stoppingToken
        );

        if (!sent)
        {
            // Do NOT purge reported data: the report did not reach anyone, so preserve it
            // for the next attempt. Still run an age-based backstop purge so the tables
            // cannot grow without bound if email stays down for days.
            _logger.LogError("Daily report send failed; metrics data preserved (age-based backstop purge only)");
            _metricsStore.Purge(TimeSpan.FromHours(SafetyRetentionHours));
            return;
        }

        // Mark today as reported before purging so a crash mid-purge cannot cause a
        // duplicate report tomorrow.
        WriteLastReportDate(LocalToday(zone));

        await PurgeAfterReportAsync();
        _logger.LogInformation("Metrics data purged after daily report");
    }

    private async Task PurgeAfterReportAsync()
    {
        long cpuFloor, ramFloor;
        if (_portalOptions.CurrentValue.Enabled)
        {
            // Only purge cpu/ram rows the portal has actually acknowledged.
            cpuFloor = await _syncState.GetLastForwardedRowIdAsync("cpu_sent");
            ramFloor = await _syncState.GetLastForwardedRowIdAsync("ram_sent");
        }
        else
        {
            // No portal: nothing to forward, so everything is purgeable.
            cpuFloor = long.MaxValue;
            ramFloor = long.MaxValue;
        }

        var result = _metricsStore.PurgeReportedData(cpuFloor, ramFloor);

        // SQLite reuses rowids from 1 once a table is emptied; reset the cursors so the
        // next batch is not skipped (cursor would otherwise be greater than every new rowid).
        if (result.CpuTableEmptied)
        {
            await _syncState.SetLastForwardedRowIdAsync("cpu", 0);
            await _syncState.SetLastForwardedRowIdAsync("cpu_sent", 0);
        }
        if (result.RamTableEmptied)
        {
            await _syncState.SetLastForwardedRowIdAsync("ram", 0);
            await _syncState.SetLastForwardedRowIdAsync("ram_sent", 0);
        }
    }

    private static DateOnly LocalToday(TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));

    private bool IsReportOwed()
    {
        var opts = _options.CurrentValue;
        if (!TimeSpan.TryParse(opts.DailyReportTime, out var reportTimeOfDay))
            reportTimeOfDay = TimeSpan.FromHours(8);

        var zone = GetConfiguredZone();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var scheduledToday = nowLocal.Date + reportTimeOfDay;

        // Owed only if today's scheduled time has passed and we have not reported today.
        return nowLocal >= scheduledToday && ReadLastReportDate() < DateOnly.FromDateTime(nowLocal.Date);
    }

    private DateOnly ReadLastReportDate()
    {
        try
        {
            if (File.Exists(_lastReportMarkerPath) &&
                DateOnly.TryParse(File.ReadAllText(_lastReportMarkerPath).Trim(), out var date))
                return date;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read last-report marker");
        }
        return DateOnly.MinValue;
    }

    private void WriteLastReportDate(DateOnly date)
    {
        try
        {
            File.WriteAllText(_lastReportMarkerPath, date.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist last-report marker");
        }
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

        var nextReportUtc = ConvertLocalToUtcSafe(nextReportLocal, zone);
        var delay = nextReportUtc - nowUtc;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static DateTime ConvertLocalToUtcSafe(DateTime localTime, TimeZoneInfo zone)
    {
        // Guard against DST spring-forward gaps where the chosen local time does not exist.
        if (zone.IsInvalidTime(localTime))
            localTime = localTime.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), zone);
    }
}
