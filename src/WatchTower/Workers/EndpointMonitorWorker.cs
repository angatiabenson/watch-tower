using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Services.Endpoints;

namespace WatchTower.Workers;

/// <summary>
/// Runs the <c>Monitoring:Endpoints</c> checks (HTTP, TCP, TLS certificate), each on its own
/// interval, records the latest result per endpoint and drives the <c>endpoint:&lt;name&gt;</c>
/// alert: Critical (EndpointDown) after <c>FailuresBeforeAlert</c> consecutive failures,
/// Warning (CertificateExpiring) when a certificate is inside its warning window, resolved
/// (EndpointRecovered) when the check passes again.
/// </summary>
public sealed class EndpointMonitorWorker(
    IEndpointProbe probe,
    IMetricsStore metricsStore,
    IAlertManager alerts,
    IOptionsMonitor<EndpointMonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<EndpointMonitorWorker> logger,
    IWorkerHealth? workerHealth = null) : BackgroundService
{
    /// <summary>How often due checks are looked for.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, DateTime> _lastRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _invalidLogged = new(StringComparer.OrdinalIgnoreCase);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Endpoint Monitor Worker started");
        workerHealth?.ReportSuccess(WorkerNames.Endpoints, Tick);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                workerHealth?.ReportSuccess(WorkerNames.Endpoints, Tick);
                await Task.Delay(Tick, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in Endpoint Monitor Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, stoppingToken);
            }
        }

        logger.LogInformation("Endpoint Monitor Worker stopped");
    }

    /// <summary>Runs every check that is due, concurrently. Exposed for testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var due = new List<EndpointCheck>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = options.CurrentValue.Endpoints;

        for (var i = 0; i < configured.Count; i++)
        {
            if (!EndpointCheck.TryCreate(configured[i], $"Monitoring:Endpoints:{i}", out var check, out var error))
            {
                if (_invalidLogged.TryAdd(error, error))
                    logger.LogWarning("Skipping invalid endpoint check: {Error}", error);
                continue;
            }
            if (!names.Add(check.Name))
                continue; // duplicate name: StartupValidator reports it

            if (!_lastRun.TryGetValue(check.Name, out var last) || now - last >= check.Interval)
                due.Add(check);
        }

        foreach (var check in due)
            _lastRun[check.Name] = now;

        await Task.WhenAll(due.Select(check => RunAsync(check, ct)));
    }

    private async Task RunAsync(EndpointCheck check, CancellationToken ct)
    {
        try
        {
            var result = await probe.ProbeAsync(check, ct);
            await HandleResultAsync(check, result, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One check's failure must not stop the others.
            logger.LogError(ex, "Endpoint check {Endpoint} failed unexpectedly", check.Name);
        }
    }

    private async Task HandleResultAsync(EndpointCheck check, EndpointProbeResult result, CancellationToken ct)
    {
        var now = UtcNow;
        metricsStore.AddEndpointStatus(new EndpointStatus(
            now, check.Name, check.Type.ToString(), check.DisplayTarget, result.State.ToString(),
            result.State != EndpointState.Down, result.ResponseTimeMs, result.CertificateDaysLeft, result.Detail));

        var key = AlertKey.Endpoint(check.Name);

        if (result.State == EndpointState.Down)
        {
            var failures = _failures.AddOrUpdate(check.Name, 1, (_, n) => n + 1);
            logger.LogWarning("Endpoint {Endpoint} check failed ({Failures}/{Threshold}): {Detail}",
                check.Name, failures, check.FailuresBeforeAlert, result.Detail);
            if (failures < check.FailuresBeforeAlert)
                return;

            await alerts.RaiseAsync(key, AlertSeverity.Critical, $"Endpoint Down: {check.Name}",
                new Incident(now, IncidentType.EndpointDown,
                    $"Endpoint '{check.Name}' ({check.Type} {check.DisplayTarget}) is down: {result.Detail} " +
                    $"({failures} consecutive failed checks)"),
                ct);
            return;
        }

        _failures.TryRemove(check.Name, out _);

        if (result.State == EndpointState.Expiring)
        {
            // Coming back from "down" to merely "expiring": close the critical alert first so the
            // warning is reported as what it is, not folded into the outage.
            if (alerts.GetOpenAlerts().Any(a => string.Equals(a.Key, key.Value, StringComparison.OrdinalIgnoreCase) &&
                                                a.Severity == AlertSeverity.Critical))
                await alerts.ResolveAsync(key, Recovered(check, result, now), ct);

            await alerts.RaiseAsync(key, AlertSeverity.Warning, $"Certificate Expiring: {check.Name}",
                new Incident(now, IncidentType.CertificateExpiring, $"Endpoint '{check.Name}' ({check.DisplayTarget}): {result.Detail}"),
                ct);
            return;
        }

        logger.LogDebug("Endpoint {Endpoint}: {Detail}", check.Name, result.Detail);
        await alerts.ResolveAsync(key, Recovered(check, result, now), ct);
    }

    private static Incident Recovered(EndpointCheck check, EndpointProbeResult result, DateTime now) =>
        new(now, IncidentType.EndpointRecovered,
            $"Endpoint '{check.Name}' ({check.Type} {check.DisplayTarget}) is OK again: {result.Detail}" +
            (result.ResponseTimeMs is { } ms ? $" in {ms:0} ms" : string.Empty));
}