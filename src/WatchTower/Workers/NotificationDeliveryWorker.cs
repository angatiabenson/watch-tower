using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

/// <summary>
/// Drives <see cref="IAlertManager.ProcessPendingAsync"/>: retries notification deliveries whose
/// backoff has elapsed (including those persisted before a restart) and sends what a maintenance
/// window held back once it ends. Monitor workers only notify when they raise or resolve, so a
/// resolved notice or a held one would otherwise never be retried.
/// </summary>
public sealed class NotificationDeliveryWorker(
    IAlertManager alerts,
    TimeProvider timeProvider,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    /// <summary>How often pending deliveries are looked at.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await alerts.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while delivering pending notifications");
            }

            try
            {
                await Task.Delay(Tick, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}