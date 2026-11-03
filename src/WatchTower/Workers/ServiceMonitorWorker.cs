using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;

namespace WatchTower.Workers;

/// <summary>
/// Watches the configured Windows services and restarts any that are down.
/// </summary>
/// <remarks>
/// Each poll only <em>checks</em> services; recovering a down service (restart delay, restart,
/// stabilisation re-check, escalation) runs as an independent per-service task. A service whose
/// recovery is still in flight is skipped by later polls, so one slow or failing restart can no
/// longer delay checks of the other services. Services that are starting/continuing are treated
/// as transitional (re-checked next poll), disabled services are recorded but never restarted,
/// and nothing is restarted during <see cref="ServiceMonitorOptions.StartupGraceSeconds"/> after
/// the agent starts, so delayed-auto-start services after a reboot are not fought over.
/// </remarks>
public class ServiceMonitorWorker : BackgroundService
{
    private readonly IWindowsServiceManager _serviceManager;
    private readonly IMetricsStore _metricsStore;
    private readonly IAlertManager _alerts;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<ServiceMonitorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServiceMonitorWorker> _logger;
    private readonly IWorkerHealth? _health;
    private readonly IMaintenanceState _maintenance;

    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeOutages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _disabledLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _recoveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _maintenanceLogged = new(StringComparer.OrdinalIgnoreCase);

    private DateTime? _monitoringStartedAt;

    public ServiceMonitorWorker(
        IWindowsServiceManager serviceManager,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<ServiceMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<ServiceMonitorWorker> logger,
        IWorkerHealth? workerHealth = null,
        IMaintenanceState? maintenance = null
    )
    {
        _serviceManager = serviceManager;
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
        _logger.LogInformation("Service Monitor Worker started");

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
                _logger.LogError(ex, "Error in Service Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        // Recovery tasks observe the stopping token; let them unwind before reporting stopped.
        await WhenRecoveriesIdleAsync();
        _logger.LogInformation("Service Monitor Worker stopped");
    }

    /// <summary>Liveness for the health-gated heartbeat (see <see cref="IWorkerHealth"/>).</summary>
    private void ReportAlive() =>
        _health?.ReportSuccess(WorkerNames.Services, TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollIntervalSeconds)));

    /// <summary>Completes once every in-flight per-service recovery has finished. Exposed for testing.</summary>
    internal Task WhenRecoveriesIdleAsync() => Task.WhenAll(_recoveries.Values);

    /// <summary>
    /// Checks every watched service once and starts recovery for newly-down ones (without
    /// waiting for it). Exposed for testing.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;

        // First poll (startup) and every poll after a configuration change: alerts of services
        // that are no longer watched would otherwise stay open forever.
        await RetireRemovedAsync(opts, ct);

        if (opts.WatchedServices.Count == 0)
        {
            _logger.LogDebug("No services configured for monitoring");
            return;
        }

        _monitoringStartedAt ??= UtcNow;
        var inStartupGrace = UtcNow - _monitoringStartedAt.Value < TimeSpan.FromSeconds(opts.StartupGraceSeconds);

        foreach (var serviceName in opts.WatchedServices.Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested)
                break;

            if (_recoveries.TryGetValue(serviceName, out var inFlight) && !inFlight.IsCompleted)
            {
                _logger.LogDebug("Service {ServiceName}: recovery in progress, skipping check", serviceName);
                continue;
            }

            try
            {
                await CheckServiceAsync(serviceName, opts, inStartupGrace, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One service's failure must not stop the remaining services being checked.
                _logger.LogError(ex, "Failed to check service {ServiceName}", serviceName);
            }
        }
    }

    internal const string RemovedReason = $"removed from {ServiceMonitorOptions.SectionName}:WatchedServices";

    /// <summary>The names polled (and used in alert keys) exactly as <see cref="PollOnceAsync"/> uses them.</summary>
    private static IEnumerable<string> Watched(ServiceMonitorOptions opts) =>
        opts.WatchedServices.Where(s => !string.IsNullOrWhiteSpace(s));

    private async Task RetireRemovedAsync(ServiceMonitorOptions opts, CancellationToken ct)
    {
        try
        {
            await _alerts.RetireUnconfiguredAsync(AlertKey.ServicePrefix, Watched(opts).Select(AlertKey.Service), RemovedReason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not retire alerts of services that are no longer watched");
        }
    }

    private async Task CheckServiceAsync(
        string serviceName, ServiceMonitorOptions opts, bool inStartupGrace, CancellationToken ct)
    {
        var status = _serviceManager.GetServiceStatus(serviceName);
        _metricsStore.AddServiceStatus(status);

        if (status.IsRunning)
        {
            _consecutiveFailures.TryRemove(serviceName, out _);
            _activeOutages.TryRemove(serviceName, out _);
            _disabledLogged.TryRemove(serviceName, out _);
            _logger.LogDebug("Service {ServiceName}: Running", serviceName);
            // Running again (e.g. started by an operator): close any open restart-failed alert.
            await _alerts.ResolveAsync(AlertKey.Service(serviceName), ct: ct);
            return;
        }

        if (IsTransitional(status))
        {
            // Starting or resuming: not down. Re-check on the next poll instead of restarting a
            // service that is already on its way up.
            _logger.LogDebug("Service {ServiceName} is {Status}; re-checking next poll", serviceName, status.Status);
            return;
        }

        if (IsDisabled(status))
        {
            if (_disabledLogged.TryAdd(serviceName, 0))
            {
                _logger.LogWarning(
                    "Service {ServiceName} is {Status} and its start type is Disabled; it will not be restarted",
                    serviceName, status.Status);
            }
            return;
        }
        _disabledLogged.TryRemove(serviceName, out _);

        if (inStartupGrace)
        {
            _logger.LogInformation(
                "Service {ServiceName} is {Status} during the startup grace period; not restarting yet",
                serviceName, status.Status);
            return;
        }

        if (_activeOutages.TryAdd(serviceName, 0))
        {
            var downIncident = new Incident(
                UtcNow,
                IncidentType.ServiceDown,
                $"Service '{status.DisplayName}' ({serviceName}) is {status.Status}"
            );
            _metricsStore.AddIncident(downIncident);
            await _portalEventPublisher.PublishIncidentAsync(downIncident, ct);
        }

        if (_maintenance.Current.Active)
        {
            // Administrators are working on the server: record the outage but leave the service alone.
            if (_maintenanceLogged.TryAdd(serviceName, 0))
                _logger.LogWarning("Service {ServiceName} is {Status}; not restarting it during the maintenance window",
                    serviceName, status.Status);
            return;
        }
        _maintenanceLogged.TryRemove(serviceName, out _);

        _logger.LogWarning("Service {ServiceName} is {Status}, attempting restart", serviceName, status.Status);

        // ServiceController calls block for up to the restart timeout; run each recovery on its
        // own task so other services keep being checked meanwhile.
        _recoveries[serviceName] = Task.Run(() => RecoverAsync(serviceName, status, opts, ct), CancellationToken.None);
    }

    private async Task RecoverAsync(
        string serviceName, ServiceStatus status, ServiceMonitorOptions opts, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(opts.RestartDelaySeconds), _timeProvider, ct);

            if (_maintenance.Current.Active)
            {
                _logger.LogWarning("Maintenance started; not restarting service {ServiceName}", serviceName);
                return;
            }

            var restarted = _serviceManager.RestartService(
                serviceName,
                TimeSpan.FromSeconds(opts.RestartTimeoutSeconds)
            );

            // Treat the restart as successful only if the service is STILL running
            // after a stabilisation delay. A crash-looping service that comes up
            // then immediately dies would otherwise report "restarted successfully"
            // every cycle and never escalate to an emergency.
            var recovered = false;
            if (restarted)
            {
                await Task.Delay(TimeSpan.FromSeconds(opts.StableStateDelaySeconds), _timeProvider, ct);
                var restartStatus = _serviceManager.GetServiceStatus(serviceName);
                _metricsStore.AddServiceStatus(restartStatus);
                recovered = restartStatus.IsRunning;
            }

            if (recovered)
            {
                _consecutiveFailures.TryRemove(serviceName, out _);
                _activeOutages.TryRemove(serviceName, out _);
                _logger.LogInformation("Service {ServiceName} restarted successfully", serviceName);

                var restartIncident = new Incident(
                    UtcNow,
                    IncidentType.ServiceRestarted,
                    $"Service '{status.DisplayName}' ({serviceName}) was found stopped and restarted successfully"
                );
                // Recorded either way; with an open alert it also resolves it (and says so).
                if (!await _alerts.ResolveAsync(AlertKey.Service(serviceName), restartIncident, ct))
                {
                    _metricsStore.AddIncident(restartIncident);
                    await _portalEventPublisher.PublishIncidentAsync(restartIncident, ct);
                }
                return;
            }

            var failures = _consecutiveFailures.AddOrUpdate(serviceName, 1, (_, count) => count + 1);
            _logger.LogError(
                "Service {ServiceName} did not recover (restart returned {Restarted}, stayed running=false). Consecutive failures: {Failures}",
                serviceName, restarted, failures);

            // A recovery that outlived the service's removal from the watch list must not re-open its alert.
            if (failures >= opts.MaxRestartAttempts &&
                Watched(_options.CurrentValue).Contains(serviceName, StringComparer.OrdinalIgnoreCase))
                await RaiseRestartFailedAsync(serviceName, failures, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Agent is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery of service {ServiceName} failed unexpectedly", serviceName);
        }
    }

    private static bool IsTransitional(ServiceStatus status) =>
        string.Equals(status.Status, "StartPending", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status.Status, "ContinuePending", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabled(ServiceStatus status) =>
        string.Equals(status.StartType, "Disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recovery keeps failing: raise (or keep open) the <c>svc:&lt;name&gt;</c> alert. Later failed
    /// attempts are quiet until the re-notify interval; the alert resolves once the service runs.
    /// </summary>
    private Task RaiseRestartFailedAsync(string serviceName, int failures, CancellationToken ct) =>
        _alerts.RaiseAsync(
            AlertKey.Service(serviceName),
            AlertSeverity.Critical,
            $"Service Down: {serviceName}",
            new Incident(
                UtcNow,
                IncidentType.ServiceRestartFailed,
                $"Service '{serviceName}' failed to restart after {failures} attempts"
            ),
            ct);
}