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
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Application Pool Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;

                if (opts.WatchedApplicationPools.Count == 0)
                {
                    _logger.LogDebug("No IIS application pools configured for monitoring");
                    await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                foreach (var applicationPoolName in opts.WatchedApplicationPools)
                {
                    var status = _applicationPoolManager.GetApplicationPoolStatus(
                        applicationPoolName
                    );
                    _metricsStore.AddApplicationPoolStatus(status);

                    if (status.IsRunning)
                    {
                        _consecutiveFailures.TryRemove(applicationPoolName, out _);
                        _activeOutages.TryRemove(applicationPoolName, out _);
                        _logger.LogDebug(
                            "Application pool {ApplicationPoolName}: Started",
                            applicationPoolName
                        );
                        continue;
                    }

                    if (_activeOutages.TryAdd(applicationPoolName, 0))
                    {
                        var downIncident = new Incident(
                            DateTime.UtcNow,
                            IncidentType.ApplicationPoolDown,
                            $"IIS application pool '{applicationPoolName}' is {status.Status}"
                        );
                        _metricsStore.AddIncident(downIncident);
                        await _portalEventPublisher.PublishIncidentAsync(downIncident, stoppingToken);
                    }

                    _logger.LogWarning(
                        "Application pool {ApplicationPoolName} is {Status}, attempting start",
                        applicationPoolName,
                        status.Status
                    );

                    await Task.Delay(TimeSpan.FromSeconds(opts.StartDelaySeconds), stoppingToken);

                    var started = _applicationPoolManager.StartApplicationPool(
                        applicationPoolName,
                        TimeSpan.FromSeconds(opts.StartTimeoutSeconds)
                    );

                    if (started)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(opts.StableStateDelaySeconds),
                            stoppingToken
                        );

                        var restartStatus = _applicationPoolManager.GetApplicationPoolStatus(
                            applicationPoolName
                        );
                        _metricsStore.AddApplicationPoolStatus(restartStatus);

                        if (restartStatus.IsRunning)
                        {
                            _consecutiveFailures.TryRemove(applicationPoolName, out _);
                            _activeOutages.TryRemove(applicationPoolName, out _);

                            _logger.LogInformation(
                                "Application pool {ApplicationPoolName} started successfully",
                                applicationPoolName
                            );

                            var startedIncident = new Incident(
                                DateTime.UtcNow,
                                IncidentType.ApplicationPoolStarted,
                                $"IIS application pool '{applicationPoolName}' was found stopped and started successfully"
                            );
                            _metricsStore.AddIncident(startedIncident);
                            await _portalEventPublisher.PublishIncidentAsync(startedIncident, stoppingToken);

                            continue;
                        }
                    }

                    var failures = _consecutiveFailures.AddOrUpdate(
                        applicationPoolName,
                        1,
                        (_, count) => count + 1
                    );

                    _logger.LogError(
                        "Failed to start application pool {ApplicationPoolName}. Consecutive failures: {Failures}",
                        applicationPoolName,
                        failures
                    );

                    if (failures >= opts.MaxStartAttempts)
                    {
                        await HandleEmergencyAsync(applicationPoolName, failures, stoppingToken);
                    }
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.CurrentValue.PollIntervalSeconds),
                    stoppingToken
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Application Pool Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Application Pool Monitor Worker stopped");
    }

    private async Task HandleEmergencyAsync(
        string applicationPoolName,
        int failures,
        CancellationToken ct
    )
    {
        var cooldown = TimeSpan.FromSeconds(
            _reportingOptions.CurrentValue.EmergencyCooldownSeconds
        );
        var lastEmail = _lastEmergencyPerPool.GetValueOrDefault(
            applicationPoolName,
            DateTime.MinValue
        );

        if (DateTime.UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.ApplicationPoolStartFailed,
            $"IIS application pool '{applicationPoolName}' failed to start after {failures} attempts"
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        await _emailService.SendEmailAsync(
            $"[WatchTower] IIS App Pool Down: {applicationPoolName}",
            html,
            ct
        );

        _lastEmergencyPerPool[applicationPoolName] = DateTime.UtcNow;
    }
}
