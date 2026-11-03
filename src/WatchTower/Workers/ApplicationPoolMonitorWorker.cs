using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
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

    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, byte> _activeOutages = new();

    public ApplicationPoolMonitorWorker(
        IIisApplicationPoolManager applicationPoolManager,
        IMetricsStore metricsStore,
        IAlertManager alerts,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<ApplicationPoolMonitorOptions> options,
        TimeProvider timeProvider,
        ILogger<ApplicationPoolMonitorWorker> logger
    )
    {
        _applicationPoolManager = applicationPoolManager;
        _metricsStore = metricsStore;
        _alerts = alerts;
        _portalEventPublisher = portalEventPublisher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Application Pool Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
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

    /// <summary>Runs a single poll/evaluate cycle over all watched pools. Exposed for testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;

        if (opts.WatchedApplicationPools.Count == 0)
        {
            _logger.LogDebug("No IIS application pools configured for monitoring");
            return;
        }

        foreach (var applicationPoolName in opts.WatchedApplicationPools)
        {
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

            _logger.LogWarning(
                "Application pool {ApplicationPoolName} is {Status}, attempting start",
                applicationPoolName, status.Status);

            await Task.Delay(TimeSpan.FromSeconds(opts.StartDelaySeconds), _timeProvider, ct);

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