using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailReporterWorker> _logger;

    // Persisted marker (survives restarts and purges) recording the local date of the
    // last successfully-sent daily report, so a restart after the scheduled time does
    // not silently skip that day's report.
    private readonly string _lastReportMarkerPath;

    // Backstop retention used only when the daily report repeatedly fails to send, so the
    // metric tables stay bounded without losing the most recent data.
    private const int SafetyRetentionHours = 48;

    /// <summary>
    /// Waits between attempts after a failed daily report send. Once exhausted, the worker
    /// waits for the next scheduled report instead.
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> RetryDelays = new[]
    {
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60),
    };

    /// <summary>Delay used between retries; replaceable in tests to observe the schedule.</summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryDelayAsync { get; set; }

    public EmailReporterWorker(
        IReportBuilder reportBuilder,
        IEmailService emailService,
        IMetricsStore metricsStore,
        IPortalSyncState syncState,
        IOptionsMonitor<ReportingOptions> options,
        IOptionsMonitor<DisplayOptions> displayOptions,
        IOptionsMonitor<PortalOptions> portalOptions,
        TimeProvider timeProvider,
        WatchTowerPaths paths,
        ILogger<EmailReporterWorker> logger
    )
    {
        _lastReportMarkerPath = paths.LastReportMarkerPath;
        _reportBuilder = reportBuilder;
        _emailService = emailService;
        _metricsStore = metricsStore;
        _syncState = syncState;
        _options = options;
        _displayOptions = displayOptions;
        _portalOptions = portalOptions;
        _timeProvider = timeProvider;
        _logger = logger;
        RetryDelayAsync = (delay, ct) => Task.Delay(delay, _timeProvider, ct);
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

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
                await RunDailyReportWithRetriesAsync(stoppingToken);
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

                await Task.Delay(delay, _timeProvider, stoppingToken);

                await RunDailyReportWithRetriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Email Reporter Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Email Reporter Worker stopped");
    }

    internal enum DailyReportOutcome
    {
        Sent,
        SendFailed,
        EmailNotConfigured,
    }

    /// <summary>
    /// Runs the daily report and, if sending fails, retries after each of
    /// <see cref="RetryDelays"/> (5, 15, 30, 60 min) before giving up until the next scheduled
    /// report. Previously a single failed send meant no report for a full day.
    /// </summary>
    internal async Task RunDailyReportWithRetriesAsync(CancellationToken stoppingToken)
    {
        // Pin the reporting day up front so a retry that succeeds after local midnight marks
        // the day it was scheduled for, not the next one (which would skip that day's report).
        var reportDate = LocalToday(GetConfiguredZone());

        var outcome = await RunDailyReportAsync(stoppingToken, reportDate);
        for (var attempt = 0; outcome == DailyReportOutcome.SendFailed && attempt < RetryDelays.Count; attempt++)
        {
            var delay = RetryDelays[attempt];
            _logger.LogWarning(
                "Daily report send failed; retry {Attempt}/{Total} in {Minutes} min",
                attempt + 1, RetryDelays.Count, delay.TotalMinutes);

            await RetryDelayAsync(delay, stoppingToken);
            outcome = await RunDailyReportAsync(stoppingToken, reportDate);
        }

        if (outcome == DailyReportOutcome.SendFailed)
            _logger.LogError("Daily report could not be sent after {Count} retries; waiting for the next scheduled report",
                RetryDelays.Count);
    }

    /// <summary>Builds and sends the daily report, purging only on confirmed delivery. Exposed for testing.</summary>
    internal async Task<DailyReportOutcome> RunDailyReportAsync(
        CancellationToken stoppingToken, DateOnly? reportDate = null)
    {
        var zone = GetConfiguredZone();

        if (!_emailService.IsConfigured)
        {
            // Nothing can be delivered: skip building the report and do not retry. The
            // backstop purge still keeps the tables bounded.
            _logger.LogWarning("Daily report skipped: SMTP is not configured (age-based backstop purge only)");
            _metricsStore.Purge(TimeSpan.FromHours(SafetyRetentionHours));
            return DailyReportOutcome.EmailNotConfigured;
        }

        _logger.LogInformation("Generating daily scheduled report");

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
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
            return DailyReportOutcome.SendFailed;
        }

        // Mark the day as reported before purging so a crash mid-purge cannot cause a
        // duplicate report tomorrow.
        WriteLastReportDate(reportDate ?? LocalToday(zone));

        await PurgeAfterReportAsync(report.GeneratedAt);
        _logger.LogInformation("Metrics data purged after daily report");
        return DailyReportOutcome.Sent;
    }

    private async Task PurgeAfterReportAsync(DateTime reportSnapshotUtc)
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

        // Incidents recorded after the report snapshot were not in it; keep them for the next one.
        var result = _metricsStore.PurgeReportedData(cpuFloor, ramFloor, reportSnapshotUtc);

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

    private DateOnly LocalToday(TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone));

    private bool IsReportOwed()
    {
        var opts = _options.CurrentValue;
        if (!TimeSpan.TryParse(opts.DailyReportTime, out var reportTimeOfDay))
            reportTimeOfDay = TimeSpan.FromHours(8);

        var zone = GetConfiguredZone();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone);
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
        var nowUtc = UtcNow;
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