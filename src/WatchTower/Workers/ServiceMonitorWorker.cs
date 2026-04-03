using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class ServiceMonitorWorker : BackgroundService
{
    private readonly IWindowsServiceManager _serviceManager;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IOptionsMonitor<ServiceMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly ILogger<ServiceMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerService = new();
    private readonly ConcurrentDictionary<string, byte> _activeOutages = new();

    public ServiceMonitorWorker(
        IWindowsServiceManager serviceManager,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IOptionsMonitor<ServiceMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        ILogger<ServiceMonitorWorker> logger
    )
    {
        _serviceManager = serviceManager;
        _metricsStore = metricsStore;
        _emailService = emailService;
        _reportBuilder = reportBuilder;
        _options = options;
        _reportingOptions = reportingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Service Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;

                if (opts.WatchedServices.Count == 0)
                {
                    _logger.LogDebug("No services configured for monitoring");
                    await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), stoppingToken);
                    continue;
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
                        _metricsStore.AddIncident(
                            new Incident(
                                DateTime.UtcNow,
                                IncidentType.ServiceDown,
                                $"Service '{status.DisplayName}' ({serviceName}) is {status.Status}"
                            )
                        );
                    }

                    _logger.LogWarning(
                        "Service {ServiceName} is {Status}, attempting restart",
                        serviceName,
                        status.Status
                    );

                    await Task.Delay(TimeSpan.FromSeconds(opts.RestartDelaySeconds), stoppingToken);

                    var restarted = _serviceManager.RestartService(
                        serviceName,
                        TimeSpan.FromSeconds(opts.RestartTimeoutSeconds)
                    );

                    if (restarted)
                    {
                        _consecutiveFailures.TryRemove(serviceName, out _);
                        _activeOutages.TryRemove(serviceName, out _);
                        _logger.LogInformation(
                            "Service {ServiceName} restarted successfully",
                            serviceName
                        );

                        var restartStatus = _serviceManager.GetServiceStatus(serviceName);
                        _metricsStore.AddServiceStatus(restartStatus);

                        // Log successful restart as incident
                        var restartIncident = new Incident(
                            DateTime.UtcNow,
                            IncidentType.ServiceRestarted,
                            $"Service '{status.DisplayName}' ({serviceName}) was found stopped and restarted successfully"
                        );
                        _metricsStore.AddIncident(restartIncident);
                    }
                    else
                    {
                        var failures = _consecutiveFailures.AddOrUpdate(
                            serviceName,
                            1,
                            (_, count) => count + 1
                        );
                        _logger.LogError(
                            "Failed to restart {ServiceName}. Consecutive failures: {Failures}",
                            serviceName,
                            failures
                        );

                        if (failures >= opts.MaxRestartAttempts)
                        {
                            await HandleEmergencyAsync(serviceName, failures, stoppingToken);
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Service Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Service Monitor Worker stopped");
    }

    private async Task HandleEmergencyAsync(string serviceName, int failures, CancellationToken ct)
    {
        var cooldown = TimeSpan.FromSeconds(
            _reportingOptions.CurrentValue.EmergencyCooldownSeconds
        );
        var lastEmail = _lastEmergencyPerService.GetValueOrDefault(serviceName, DateTime.MinValue);

        if (DateTime.UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.ServiceRestartFailed,
            $"Service '{serviceName}' failed to restart after {failures} attempts"
        );

        _metricsStore.AddIncident(incident);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        await _emailService.SendEmailAsync($"[WatchTower] Service Down: {serviceName}", html, ct);

        _lastEmergencyPerService[serviceName] = DateTime.UtcNow;
    }
}
