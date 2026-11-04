using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

public class ApplicationPoolMonitorWorker : BackgroundService
{
    private readonly IIisApplicationPoolManager _applicationPoolManager;
    private readonly IMetricsStore _metricsStore;
    private readonly IAlertManager _alerts;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<ApplicationPoolMonitorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApplicationPoolMonitorWorker> _logger;
    private readonly IWorkerHealth? _health;
    private readonly IMaintenanceState _maintenance;

    // IIS application pool names are case-insensitive, like the alert keys and snapshot rows.
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeOutages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _maintenanceLogged = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationPoolMonitorWorker(
        IIisApplicationPoolManager applicationPoolManager,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<ApplicationPoolMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<ApplicationPoolMonitorWorker> logger,
        IWorkerHealth? workerHealth = null,
        IMaintenanceState? maintenance = null
    )
    {
        _applicationPoolManager = applicationPoolManager;
        _metricsStore = metricsStore;
        _alerts = alerts;
        _portalEventPublisher = portalEventPublisher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _health = workerHealth;
        _maintenance = maintenance ?? NoMaintenance.Instance;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Application Pool Monitor Worker started");

        ReportAlive();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                ReportAlive();
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)),
                    _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Application Pool Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Application Pool Monitor Worker stopped");
    }

    /// <summary>Liveness for the health-gated heartbeat (see <see cref="IWorkerHealth"/>).</summary>
    private void ReportAlive() =>
        _health?.ReportSuccess(WorkerNames.AppPools, TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)));

    internal const string RemovedReason = $"removed from {ApplicationPoolMonitorOptions.SectionName}:WatchedApplicationPools";

    /// <summary>
    /// The pools polled, in alert keys and in snapshot rows: blank entries skipped, and the first
    /// spelling of names that only differ in case (IIS pool names are case-insensitive).
    /// </summary>
    private static List<string> Watched(ApplicationPoolMonitorOptions opts) =>
        opts.WatchedApplicationPools.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Runs a single poll/evaluate cycle over all watched pools. Exposed for testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var watched = Watched(opts);

        // First poll (startup) and every poll after a configuration change: alerts of pools that
        // are no longer watched would otherwise stay open forever.
        try
        {
            await _alerts.RetireUnconfiguredAsync(AlertKey.AppPoolPrefix, watched.Select(AlertKey.AppPool), RemovedReason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not retire alerts of application pools that are no longer watched");
        }

        // Their last status would otherwise be repeated in every metrics batch forever.
        try
        {
            foreach (var removed in _metricsStore.RemoveSnapshotsExcept(SnapshotKind.ApplicationPool, watched))
                _logger.LogInformation("Stopped reporting application pool {ApplicationPoolName} to the portal: {Reason}", removed, RemovedReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove the snapshots of application pools that are no longer watched");
        }

        if (watched.Count == 0)
        {
            _logger.LogDebug("No IIS application pools configured for monitoring");
            return;
        }

        foreach (var applicationPoolName in watched)
        {
            // A stopped pool can take StartDelay + StartTimeout + StableStateDelay to handle: report
            // progress per pool so a long cycle over several pools is not mistaken for a dead worker.
            ReportAlive();

            var status = await _applicationPoolManager.GetApplicationPoolStatusAsync(applicationPoolName, ct);
            _metricsStore.AddApplicationPoolStatus(status);

            if (status.IsRunning)
            {
                _consecutiveFailures.TryRemove(applicationPoolName, out _);
                _activeOutages.TryRemove(applicationPoolName, out _);
                _logger.LogDebug("Application pool {ApplicationPoolName}: Started", applicationPoolName);
                await _alerts.ResolveAsync(AlertKey.AppPool(applicationPoolName), ct: ct);
                continue;
            }

            if (ApplicationPoolStatus.IsUnknown(status.Status))
            {
                // The state query timed out or failed (IIS busy, WAS slow, PowerShell module
                // load stalled). That says nothing about the pool, so it must not raise a
                // "down" incident or trigger a start; re-check on the next poll.
                _logger.LogWarning(
                    "State of application pool {ApplicationPoolName} could not be determined; re-checking next poll",
                    applicationPoolName);
                continue;
            }

            if (_activeOutages.TryAdd(applicationPoolName, 0))
            {
                var downIncident = new Incident(
                    UtcNow,
                    IncidentType.ApplicationPoolDown,
                    $"IIS application pool '{applicationPoolName}' is {status.Status}"
                );
                _metricsStore.AddIncident(downIncident);
                await _portalEventPublisher.PublishIncidentAsync(downIncident, ct);
            }

            if (_maintenance.Current.Active)
            {
                // Administrators are working on the server: record the outage but leave the pool alone.
                if (_maintenanceLogged.TryAdd(applicationPoolName, 0))
                    _logger.LogWarning(
                        "Application pool {ApplicationPoolName} is {Status}; not starting it during the maintenance window",
                        applicationPoolName, status.Status);
                continue;
            }
            _maintenanceLogged.TryRemove(applicationPoolName, out _);

            _logger.LogWarning(
                "Application pool {ApplicationPoolName} is {Status}, attempting start",
                applicationPoolName, status.Status);

            await Task.Delay(TimeSpan.FromSeconds(opts.StartDelaySeconds), _timeProvider, ct);

            // A maintenance window may have opened during the delay: decide again right before
            // touching the pool, as the check above only covered the moment the outage was seen.
            if (_maintenance.Current.Active)
            {
                _maintenanceLogged.TryAdd(applicationPoolName, 0);
                _logger.LogWarning("Maintenance started; not starting application pool {ApplicationPoolName}", applicationPoolName);
                continue;
            }

            var started = await _applicationPoolManager.StartApplicationPoolAsync(
                applicationPoolName,
                TimeSpan.FromSeconds(opts.StartTimeoutSeconds),
                ct
            );

            if (started)
            {
                await Task.Delay(TimeSpan.FromSeconds(opts.StableStateDelaySeconds), _timeProvider, ct);

                var restartStatus = await _applicationPoolManager.GetApplicationPoolStatusAsync(applicationPoolName, ct);
                _metricsStore.AddApplicationPoolStatus(restartStatus);

                if (ApplicationPoolStatus.IsUnknown(restartStatus.Status))
                {
                    // Inconclusive re-check: neither a success nor a failed attempt.
                    _logger.LogWarning(
                        "Could not confirm application pool {ApplicationPoolName} is running after start; re-checking next poll",
                        applicationPoolName);
                    continue;
                }

                if (restartStatus.IsRunning)
                {
                    _consecutiveFailures.TryRemove(applicationPoolName, out _);
                    _activeOutages.TryRemove(applicationPoolName, out _);

                    _logger.LogInformation(
                        "Application pool {ApplicationPoolName} started successfully", applicationPoolName);

                    var startedIncident = new Incident(
                        UtcNow,
                        IncidentType.ApplicationPoolStarted,
                        $"IIS application pool '{applicationPoolName}' was found stopped and started successfully"
                    );
                    if (!await _alerts.ResolveAsync(AlertKey.AppPool(applicationPoolName), startedIncident, ct))
                    {
                        _metricsStore.AddIncident(startedIncident);
                        await _portalEventPublisher.PublishIncidentAsync(startedIncident, ct);
                    }

                    continue;
                }
            }

            var failures = _consecutiveFailures.AddOrUpdate(applicationPoolName, 1, (_, count) => count + 1);

            _logger.LogError(
                "Failed to start application pool {ApplicationPoolName}. Consecutive failures: {Failures}",
                applicationPoolName, failures);

            if (failures >= opts.MaxStartAttempts)
                await RaiseStartFailedAsync(applicationPoolName, failures, ct);
        }
    }

    /// <summary>Start attempts keep failing: raise (or keep open) the <c>pool:&lt;name&gt;</c> alert.</summary>
    private Task RaiseStartFailedAsync(string applicationPoolName, int failures, CancellationToken ct) =>
        _alerts.RaiseAsync(
            AlertKey.AppPool(applicationPoolName),
            AlertSeverity.Critical,
            $"IIS App Pool Down: {applicationPoolName}",
            new Incident(
                UtcNow,
                IncidentType.ApplicationPoolStartFailed,
                $"IIS application pool '{applicationPoolName}' failed to start after {failures} attempts"
            ),
            ct);
}