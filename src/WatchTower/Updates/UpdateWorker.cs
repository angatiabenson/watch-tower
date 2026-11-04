using Microsoft.Extensions.Options;

namespace WatchTower.Updates;

/// <summary>Checks the feed periodically, optionally pre-downloads, and fires the restart countdown.</summary>
public sealed class UpdateWorker(
    UpdateService updates,
    IOptionsMonitor<UpdateOptions> options,
    TimeProvider time,
    ILogger<UpdateWorker> logger) : BackgroundService
{
    // Let the agent settle before the first network call after a (re)start.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCheck = time.GetUtcNow() + InitialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(nextCheck, stoppingToken);
                if (time.GetUtcNow() >= nextCheck)
                    nextCheck = time.GetUtcNow() + TimeSpan.FromHours(Math.Clamp(options.CurrentValue.CheckIntervalHours, 1, 168));

                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in Update Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
            }
        }
    }

    internal async Task TickAsync(DateTimeOffset nextCheck, CancellationToken ct)
    {
        if (updates.CountdownElapsed())
        {
            // A transient refusal (a check in progress) is retried next second; a permanent one
            // (e.g. console mode) cancels the countdown instead of retrying forever.
            if (!updates.TryInstallNow(out var message) && !updates.GetStatus().CanInstall)
                updates.CancelCountdown();
            logger.LogInformation("{Message}", message);
            return;
        }

        var opts = options.CurrentValue;
        if (!opts.Enabled || time.GetUtcNow() < nextCheck)
            return;

        var status = await updates.CheckAsync(ct);
        if (opts.AutoDownload && status.Phase == UpdatePhase.Available)
            updates.TryStartDownload(countdown: false, out _);
    }
}