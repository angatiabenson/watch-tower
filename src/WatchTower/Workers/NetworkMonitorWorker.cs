using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class NetworkMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IOptionsMonitor<NetworkMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly ILogger<NetworkMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerInterface = new();

    public NetworkMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IOptionsMonitor<NetworkMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        ILogger<NetworkMonitorWorker> logger)
    {
        _metricsProvider = metricsProvider;
        _metricsStore = metricsStore;
        _emailService = emailService;
        _reportBuilder = reportBuilder;
        _options = options;
        _reportingOptions = reportingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Network Monitor Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                var networkMetrics = _metricsProvider.GetNetworkMetrics();

                foreach (var metric in networkMetrics)
                {
                    _metricsStore.AddNetworkMetric(metric);

                    if (metric.OutputQueueLength > opts.OutputQueueWarningThreshold)
                    {
                        _logger.LogWarning("NETWORK QUEUE SATURATED: {Interface} output queue {Queue:F0} (threshold: > {Threshold})",
                            metric.InterfaceName, metric.OutputQueueLength, opts.OutputQueueWarningThreshold);
                        await HandleEmergencyAsync(metric, stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug("Network {Interface}: output queue {Queue:F0}", metric.InterfaceName, metric.OutputQueueLength);
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
                _logger.LogError(ex, "Error in Network Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Network Monitor Worker stopped");
    }

    private async Task HandleEmergencyAsync(NetworkMetric metric, CancellationToken ct)
    {
        var cooldown = TimeSpan.FromSeconds(_reportingOptions.CurrentValue.EmergencyCooldownSeconds);
        var lastEmail = _lastEmergencyPerInterface.GetValueOrDefault(metric.InterfaceName, DateTime.MinValue);

        if (DateTime.UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.NetworkQueueSaturated,
            $"Network interface '{metric.InterfaceName}' output queue at {metric.OutputQueueLength:F0} (threshold: > {_options.CurrentValue.OutputQueueWarningThreshold})");

        _metricsStore.AddIncident(incident);

        var report = _reportBuilder.BuildReport(TimeSpan.FromHours(24));
        var html = _reportBuilder.RenderEmergencyReportHtml(report, incident);
        await _emailService.SendEmailAsync($"[WatchTower] Network Saturated: {metric.InterfaceName}", html, ct);

        _lastEmergencyPerInterface[metric.InterfaceName] = DateTime.UtcNow;
    }
}
