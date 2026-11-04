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
        ReportAlive();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                ReportAlive();
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

    /// <summary>Slack on top of the longest check timeout (connection setup, redirects, recording).</summary>
    internal static readonly TimeSpan CycleSlack = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Liveness for the health-gated heartbeat (see <see cref="IWorkerHealth"/>). A cycle waits
    /// for every due check, so a healthy one can take as long as the longest check timeout: the
    /// reported interval covers that, and a loop that hangs or keeps failing is still reported
    /// stale after <see cref="WorkerHealthRegistry.StaleAfter"/> of it.
    /// </summary>
    private void ReportAlive() => workerHealth?.ReportSuccess(WorkerNames.Endpoints, LivenessInterval(options.CurrentValue));

    internal static TimeSpan LivenessInterval(EndpointMonitorOptions opts)
    {
        var longest = opts.Endpoints.Count == 0
            ? 0
            : opts.Endpoints.Max(e => Math.Clamp(e.TimeoutSeconds, 1, EndpointCheck.MaxTimeoutSeconds));
        var cycle = TimeSpan.FromSeconds(longest) + CycleSlack;
        return cycle > Tick ? cycle : Tick;
    }

    /// <summary>Runs every check that is due, concurrently. Exposed for testing.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var due = new List<EndpointCheck>();
        // Checks that run: their schedule, failure count and alert.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Every configured name, valid or not: only a name that is gone loses its snapshot row.
        var configuredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configured = options.CurrentValue.Endpoints;

        for (var i = 0; i < configured.Count; i++)
        {
            if (configured[i] is not { } entry)
                continue;
            var rawName = entry.Name?.Trim();
            if (!string.IsNullOrEmpty(rawName))
                configuredNames.Add(rawName);

            if (!EndpointCheck.TryCreate(entry, $"Monitoring:Endpoints:{i}", out var check, out var error))
            {
                if (_invalidLogged.TryAdd(error, error))
                    logger.LogWarning("Skipping invalid endpoint check: {Error}", error);
                if (!string.IsNullOrEmpty(rawName))
                    invalid.TryAdd(rawName, error);
                continue;
            }
            if (!names.Add(check.Name))
                continue; // duplicate name: StartupValidator reports it

            if (!_lastRun.TryGetValue(check.Name, out var last) || now - last >= check.Interval)
                due.Add(check);
        }

        foreach (var check in due)
            _lastRun[check.Name] = now;

        await ReconcileAsync(names, configuredNames, ct);
        MarkMisconfigured(invalid.Where(e => !names.Contains(e.Key)));

        await Task.WhenAll(due.Select(check => RunAsync(check, ct)));
    }

    internal const string RemovedReason = $"removed from {EndpointMonitorOptions.SectionName}:Endpoints or no longer a valid check";

    internal const string SnapshotRemovedReason = $"removed from {EndpointMonitorOptions.SectionName}:Endpoints";

    /// <summary>
    /// First poll (startup) and every poll after a configuration change: forgets the schedule of
    /// checks that no longer run (so a check added again, or fixed, runs at once), retires their
    /// alerts, which could otherwise never resolve, and deletes the snapshot rows of names that
    /// are no longer configured at all.
    /// </summary>
    private async Task ReconcileAsync(HashSet<string> running, HashSet<string> configuredNames, CancellationToken ct)
    {
        foreach (var name in _lastRun.Keys.Concat(_failures.Keys).Where(n => !running.Contains(n)).ToList())
        {
            _lastRun.TryRemove(name, out _);
            _failures.TryRemove(name, out _);
        }

        try
        {
            await alerts.RetireUnconfiguredAsync(AlertKey.EndpointPrefix, running.Select(AlertKey.Endpoint), RemovedReason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not retire alerts of endpoint checks that are no longer configured");
        }

        // Their last result would otherwise be repeated in every metrics batch forever. A check
        // that is configured but (perhaps only for a moment) invalid keeps its row: see MarkMisconfigured.
        try
        {
            foreach (var removed in metricsStore.RemoveSnapshotsExcept(SnapshotKind.Endpoint, configuredNames))
                logger.LogInformation("Stopped reporting endpoint check {Endpoint} to the portal: {Reason}", removed, SnapshotRemovedReason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the snapshots of endpoint checks that are no longer configured");
        }
    }

    /// <summary>
    /// Keeps the last result of each configured but invalid check and marks it
    /// <see cref="EndpointStatus.ConfigurationError"/>, with the error in its detail, so the
    /// portal and the console show why it is not being checked. Checks without a result yet
    /// get no row.
    /// </summary>
    private void MarkMisconfigured(IEnumerable<KeyValuePair<string, string>> invalid)
    {
        var errors = invalid.ToList();
        if (errors.Count == 0)
            return;

        try
        {
            var rows = metricsStore.GetLatestEndpointStatuses()
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var (name, error) in errors)
            {
                if (!rows.TryGetValue(name, out var row))
                    continue;

                var marked = MarkConfigurationError(row, error);
                if (marked != row)
                    metricsStore.AddEndpointStatus(marked);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark the snapshots of invalid endpoint checks");
        }
    }

    internal static EndpointStatus MarkConfigurationError(EndpointStatus row, string error)
    {
        string lastResult;
        if (row.Status == EndpointStatus.ConfigurationError)
        {
            // Already marked: keep the last real result it carries.
            var at = row.Detail?.IndexOf(EndpointStatus.LastResultSeparator, StringComparison.Ordinal) ?? -1;
            lastResult = at < 0 ? string.Empty : row.Detail![at..];
        }
        else
        {
            lastResult = EndpointStatus.LastResultSeparator + row.Status +
                         (string.IsNullOrEmpty(row.Detail) ? string.Empty : $": {row.Detail}");
        }

        return row with
        {
            Status = EndpointStatus.ConfigurationError,
            Detail = EndpointStatus.ConfigurationErrorPrefix + error + lastResult,
        };
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