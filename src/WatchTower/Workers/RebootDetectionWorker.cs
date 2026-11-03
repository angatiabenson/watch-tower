using System.Globalization;

using WatchTower.Models;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

/// <summary>Current boot time of the machine.</summary>
public interface IBootTimeProvider
{
    DateTimeOffset GetBootTimeUtc();
}

/// <summary>
/// Boot time from the system tick count (<see cref="Environment.TickCount64"/>: milliseconds
/// since boot) — no WMI. It moves with wall-clock corrections, which
/// <see cref="RebootDetectionWorker.Tolerance"/> absorbs.
/// </summary>
public sealed class TickCountBootTimeProvider(TimeProvider time) : IBootTimeProvider
{
    public DateTimeOffset GetBootTimeUtc() => time.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>
/// Notices that the server restarted since WatchTower last ran and sends a one-off "Server
/// restarted at …" notification: Info when WatchTower had been stopped cleanly beforehand (an
/// orderly shutdown or reboot), Warning when it had not (crash, power loss, forced reset).
/// The last boot time and the clean-stop marker are kept in <c>agent_state</c>.
/// </summary>
public sealed class RebootDetectionWorker(
    IAlertStateStore state,
    IAlertManager alerts,
    IBootTimeProvider bootTime,
    TimeProvider time,
    ILogger<RebootDetectionWorker> logger) : BackgroundService
{
    internal const string LastBootKey = "system.last_boot_utc";
    internal const string CleanShutdownKey = "agent.clean_shutdown";

    /// <summary>Boot times closer than this are the same boot (tick/clock jitter, NTP corrections).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CheckAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check whether the server restarted");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        MarkCleanShutdown();
    }

    /// <summary>Records that WatchTower is stopping in an orderly way. Exposed for testing.</summary>
    internal void MarkCleanShutdown()
    {
        try
        {
            state.SetValue(CleanShutdownKey, "true");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record the clean shutdown marker");
        }
    }

    /// <summary>Compares the current boot with the last one seen. Exposed for testing.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        var currentBoot = bootTime.GetBootTimeUtc();
        var lastBootText = state.GetValue(LastBootKey);
        var stoppedCleanly = string.Equals(state.GetValue(CleanShutdownKey), "true", StringComparison.OrdinalIgnoreCase);

        // Persist first so the notification goes out at most once per boot, whatever happens next.
        // The boot time is re-recorded on every start, so slow drift between the tick count and
        // the wall clock over a long uptime never accumulates into a false "restarted".
        state.SetValue(CleanShutdownKey, "false");
        state.SetValue(LastBootKey, currentBoot.ToString("O", CultureInfo.InvariantCulture));

        if (lastBootText is null ||
            !DateTimeOffset.TryParse(lastBootText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastBoot))
            return; // first run: nothing to compare with

        if (currentBoot - lastBoot < Tolerance)
            return; // same boot: only WatchTower restarted (or the clock was nudged)

        var at = currentBoot.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var (severity, title, description) = stoppedCleanly
            ? (AlertSeverity.Info, "Server restarted",
                $"The server restarted at {at}. WatchTower was stopped cleanly beforehand (orderly shutdown or reboot).")
            : (AlertSeverity.Warning, "Server restarted unexpectedly",
                $"The server restarted at {at}. WatchTower was not stopped cleanly before it: " +
                "possible crash, power loss or forced reset.");

        logger.LogWarning("{Title}: {Description}", title, description);
        await alerts.NotifyEventAsync(AlertKey.Reboot, severity, title,
            new Incident(time.GetUtcNow().UtcDateTime, IncidentType.ServerRestarted, description), ct);
    }
}