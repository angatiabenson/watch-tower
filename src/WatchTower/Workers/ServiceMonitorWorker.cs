using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class ServiceMonitorWorker : BackgroundService
{
    private readonly IWindowsServiceManager _serviceManager;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<ServiceMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServiceMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerService = new();
    private readonly ConcurrentDictionary<string, byte> _activeOutages = new();

    public ServiceMonitorWorker(
        IWindowsServiceManager serviceManager,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<ServiceMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        TimeProvider timeProvider,
        ILogger<ServiceMonitorWorker> logger
    )
    {
        _serviceManager = serviceManager;
        _metricsStore = metricsStore;
        _emailService = emailService;
        _reportBuilder = reportBuilder;
        _portalEventPublisher = portalEventPublisher;
        _options = options;
        _reportingOptions = reportingOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Service Monitor Worker started");

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
                _logger.LogError(ex, "Error in Service Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Service Monitor Worker stopped");
    }

    /// <summary>Runs a single poll/evaluate cycle over all watched services. Exposed for testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;

        if (opts.WatchedServices.Count == 0)
        {
            _logger.LogDebug("No services configured for monitoring");
            return;
        }

        foreach (var serviceName in opts.WatchedServices)
        {
            var status = _serviceManager.GetServiceStatus(serviceName);
            _metricsStore.AddServiceStatus(status);

            if (status.IsRunning)
            {
                _consecutiveFailures.TryRemove(serviceName, out _);
                _activeOutages.TryRemove(serviceName, out _);
                _logger.LogDebug("Service {ServiceName}: Running", serviceName);
                continue;
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

            _logger.LogWarning("Service {ServiceName} is {Status}, attempting restart", serviceName, status.Status);

            await Task.Delay(TimeSpan.FromSeconds(opts.RestartDelaySeconds), _timeProvider, ct);

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
                _metricsStore.AddIncident(restartIncident);
                await _portalEventPublisher.PublishIncidentAsync(restartIncident, ct);
            }
            else
            {
                var failures = _consecutiveFailures.AddOrUpdate(serviceName, 1, (_, count) => count + 1);
                _logger.LogError(
                    "Service {ServiceName} did not recover (restart returned {Restarted}, stayed running=false). Consecutive failures: {Failures}",
                    serviceName, restarted, failures);

                if (failures >= opts.MaxRestartAttempts)
                    await HandleEmergencyAsync(serviceName, failures, ct);
            }
        }
    }

    private async Task HandleEmergencyAsync(string serviceName, int failures, CancellationToken ct)
    {
        var cooldown = TimeSpan.FromSeconds(_reportingOptions.CurrentValue.EmergencyCooldownSeconds);
        var lastEmail = _lastEmergencyPerService.GetValueOrDefault(serviceName, DateTime.MinValue);

        if (UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            UtcNow,
            IncidentType.ServiceRestartFailed,
            $"Service '{serviceName}' failed to restart after {failures} attempts"
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);

        // Per-service cooldown starts once the incident is recorded, regardless of the email outcome.
        _lastEmergencyPerService[serviceName] = UtcNow;

        await EmergencyEmail.TrySendAsync(
            _emailService, _reportBuilder, incident, $"[WatchTower] Service Down: {serviceName}", _logger, ct);
    }
}