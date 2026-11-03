using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class ApplicationPoolMonitorWorker : BackgroundService
{
    private readonly IIisApplicationPoolManager _applicationPoolManager;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<ApplicationPoolMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApplicationPoolMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerPool = new();
    private readonly ConcurrentDictionary<string, byte> _activeOutages = new();

    public ApplicationPoolMonitorWorker(
        IIisApplicationPoolManager applicationPoolManager,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<ApplicationPoolMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        TimeProvider timeProvider,
        ILogger<ApplicationPoolMonitorWorker> logger
    )
    {
        _applicationPoolManager = applicationPoolManager;
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
            var status = _applicationPoolManager.GetApplicationPoolStatus(applicationPoolName);
            _metricsStore.AddApplicationPoolStatus(status);

            if (status.IsRunning)
            {
                _consecutiveFailures.TryRemove(applicationPoolName, out _);
                _activeOutages.TryRemove(applicationPoolName, out _);
                _logger.LogDebug("Application pool {ApplicationPoolName}: Started", applicationPoolName);
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

            var started = _applicationPoolManager.StartApplicationPool(
                applicationPoolName,
                TimeSpan.FromSeconds(opts.StartTimeoutSeconds)
            );

            if (started)
            {
                await Task.Delay(TimeSpan.FromSeconds(opts.StableStateDelaySeconds), _timeProvider, ct);

                var restartStatus = _applicationPoolManager.GetApplicationPoolStatus(applicationPoolName);
                _metricsStore.AddApplicationPoolStatus(restartStatus);

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
                    _metricsStore.AddIncident(startedIncident);
                    await _portalEventPublisher.PublishIncidentAsync(startedIncident, ct);

                    continue;
                }
            }

            var failures = _consecutiveFailures.AddOrUpdate(applicationPoolName, 1, (_, count) => count + 1);

            _logger.LogError(
                "Failed to start application pool {ApplicationPoolName}. Consecutive failures: {Failures}",
                applicationPoolName, failures);

            if (failures >= opts.MaxStartAttempts)
                await HandleEmergencyAsync(applicationPoolName, failures, ct);
        }
    }

    private async Task HandleEmergencyAsync(
        string applicationPoolName,
        int failures,
        CancellationToken ct
    )
    {
        var cooldown = TimeSpan.FromSeconds(_reportingOptions.CurrentValue.EmergencyCooldownSeconds);
        var lastEmail = _lastEmergencyPerPool.GetValueOrDefault(applicationPoolName, DateTime.MinValue);

        if (UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            UtcNow,
            IncidentType.ApplicationPoolStartFailed,
            $"IIS application pool '{applicationPoolName}' failed to start after {failures} attempts"
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        var sent = await _emailService.SendEmailAsync(
            $"[WatchTower] IIS App Pool Down: {applicationPoolName}",
            html,
            ct
        );

        if (sent)
            _lastEmergencyPerPool[applicationPoolName] = UtcNow;
    }
}