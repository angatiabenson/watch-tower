using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class NetworkMonitorWorker : BackgroundService
{
    private readonly ISystemMetricsProvider _metricsProvider;
    private readonly IMetricsStore _metricsStore;
    private readonly IEmailService _emailService;
    private readonly IReportBuilder _reportBuilder;
    private readonly IPortalEventPublisher _portalEventPublisher;
    private readonly IOptionsMonitor<NetworkMonitorOptions> _options;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NetworkMonitorWorker> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastEmergencyPerInterface = new();

    public NetworkMonitorWorker(
        ISystemMetricsProvider metricsProvider,
        IMetricsStore metricsStore,
        IEmailService emailService,
        IReportBuilder reportBuilder,
        IPortalEventPublisher portalEventPublisher,
        IOptionsMonitor<NetworkMonitorOptions> options,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        TimeProvider timeProvider,
        ILogger<NetworkMonitorWorker> logger
    )
    {
        _metricsProvider = metricsProvider;
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
        _logger.LogInformation("Network Monitor Worker started");

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
                _logger.LogError(ex, "Error in Network Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Network Monitor Worker stopped");
    }

    /// <summary>Runs a single poll/evaluate cycle. Exposed for deterministic testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        IReadOnlyList<NetworkMetric> networkMetrics;
        try
        {
            networkMetrics = _metricsProvider.GetNetworkMetrics();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading network metrics failed; skipping this cycle");
            return;
        }

        foreach (var metric in networkMetrics)
        {
            _metricsStore.AddNetworkMetric(metric);

            if (metric.OutputQueueLength > opts.OutputQueueWarningThreshold)
            {
                _logger.LogWarning(
                    "NETWORK QUEUE SATURATED: {Interface} output queue {Queue:F0} (threshold: > {Threshold})",
                    metric.InterfaceName, metric.OutputQueueLength, opts.OutputQueueWarningThreshold);
                await HandleEmergencyAsync(metric, ct);
            }
            else
            {
                _logger.LogDebug(
                    "Network {Interface}: output queue {Queue:F0}",
                    metric.InterfaceName, metric.OutputQueueLength);
            }
        }
    }

    private async Task HandleEmergencyAsync(NetworkMetric metric, CancellationToken ct)
    {
        var cooldown = TimeSpan.FromSeconds(_reportingOptions.CurrentValue.EmergencyCooldownSeconds);
        var lastEmail = _lastEmergencyPerInterface.GetValueOrDefault(metric.InterfaceName, DateTime.MinValue);

        if (UtcNow - lastEmail < cooldown)
            return;

        var incident = new Incident(
            UtcNow,
            IncidentType.NetworkQueueSaturated,
            $"Network interface '{metric.InterfaceName}' output queue at {metric.OutputQueueLength:F0} (threshold: > {_options.CurrentValue.OutputQueueWarningThreshold})"
        );

        _metricsStore.AddIncident(incident);
        await _portalEventPublisher.PublishIncidentAsync(incident, ct);

        // Per-interface cooldown starts once the incident is recorded, regardless of the email outcome.
        _lastEmergencyPerInterface[metric.InterfaceName] = UtcNow;

        await EmergencyEmail.TrySendAsync(
            _emailService, _reportBuilder, incident,
            $"[WatchTower] Network Saturated: {metric.InterfaceName}", _logger, ct);
    }
}