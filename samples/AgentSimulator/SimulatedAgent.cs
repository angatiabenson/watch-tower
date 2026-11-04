// A simulated WatchTower agent. The HTTP calls go through the agent's real HttpsPortalClient;
// this file reproduces the behaviour AROUND those calls (outbox, command handling, config sync)
// as the agent implements it in src/WatchTower/Workers. Only the timings are shorter by default
// so a test run takes minutes instead of hours; every one of them can be set on the command line.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Services;

namespace WatchTower.Samples.AgentSimulator;

public sealed class SimulatorSettings
{
    public string Version { get; set; } = "0.0.0-sim";
    public string MachineName { get; set; } = "SIM-SERVER";
    public List<string> WatchedServices { get; set; } = ["W3SVC", "Spooler"];
    public List<string> WatchedPools { get; set; } = ["DefaultAppPool"];

    /// <summary>As VisibilityPortal:CommandPoll:AllowPortalManagedTargets on the agent (default false).</summary>
    public bool AllowPortalManagedTargets { get; set; }

    /// <summary>Version the simulated update feed offers (null = up to date).</summary>
    public string? AvailableVersion { get; set; }

    // Agent defaults in brackets.
    public int RetryBackoffSeconds { get; set; } = 10;          // [60] metrics batch retry, x attempt
    public int MaxRetryAttempts { get; set; } = 5;              // [5] then the batch is dropped
    public int EventRetryBaseSeconds { get; set; } = 5;         // [30] doubles per attempt
    public int EventRetryMaxSeconds { get; set; } = 60;         // [900]
    public int MaxBatchSize { get; set; } = 500;                // [500]
    public int MaxCommandAgeSeconds { get; set; } = 3600;       // [3600]
    public int ClockSkewSeconds { get; set; } = 300;            // [300]
    public bool RequireCommandIssuedAt { get; set; }            // [false] reject commands without issuedAt
    public int ResultRetryBaseSeconds { get; set; } = 5;        // [60] first retry of a refused result, doubling
    public int ResultRetryMaxDelaySeconds { get; set; } = 60;   // [1800]
    public int ResultRetryMaxSeconds { get; set; } = 600;       // [604800 = CommandPoll:ResultRetryMaxHours 168] then given up
    public int MaxResultsPerCycle { get; set; } = 50;           // [50]
}

/// <summary>A command result the portal has not accepted yet, on its own retry schedule.</summary>
public sealed class PendingResult
{
    public required CommandResultDto Result { get; init; }
    public required DateTime ProducedAt { get; init; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
}

/// <summary>One queued outbox message: a metrics batch or an event, retried like the agent does.</summary>
public sealed class OutboxItem
{
    public required long Id { get; init; }
    public required object Payload { get; init; }
    public required int Priority { get; init; } // events 1, metrics 0: events are sent first
    public required DateTime CreatedAt { get; init; }
    public int Attempts { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public bool IsEvent => Payload is AgentEventDto;
}

public sealed record FlushReport(int Sent, int Dropped, bool Failed, int Remaining);

public sealed partial class SimulatedAgent(
    IPortalClient portal, PortalOptions options, SimulatorSettings settings, ILogger log, TimeProvider time)
{
    private readonly Random _rng = new();
    private readonly List<OutboxItem> _outbox = [];
    private readonly List<CpuMetricDto> _cpu = [];
    private readonly List<RamMetricDto> _ram = [];
    private readonly Dictionary<string, (string Status, bool Running)> _services =
        settings.WatchedServices.ToDictionary(s => s, _ => ("Running", true), StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Status, bool Running)> _pools =
        settings.WatchedPools.ToDictionary(p => p, _ => ("Started", true), StringComparer.OrdinalIgnoreCase);
    // Like the agent's command_ids table: a known id is never executed again (the agent keeps
    // ids for CommandIdRetentionDays, 365 days, and at most CommandIdMaxCount; this process forever).
    private readonly HashSet<string> _seenCommands = new(StringComparer.Ordinal);
    private bool _warnedMissingIssuedAt;
    private readonly List<AgentCommandDto> _scheduled = [];
    private readonly List<PendingResult> _unsentResults = [];
    private EndpointStatusDto? _lastEndpoint;
    private string? _endpointConfigError;
    private DiskMetricDto? _lastDriveD;
    private bool _driveDReady = true;
    private long _nextId;
    private double _cpuLevel = 18, _ramLevel = 55;

    public DateTimeOffset StartedAt { get; private set; } = time.GetUtcNow();
    public string Version { get; private set; } = settings.Version;
    public string UpdatePhase { get; private set; } = settings.AvailableVersion is null ? "UpToDate" : "Available";
    public string? ConfigVersion { get; private set; }
    public string? UnacknowledgedConfigVersion { get; private set; }
    public RemoteAgentConfigDto? Config { get; private set; }
    public bool SnapshotRequested { get; set; }
    public bool ConfigSyncRequested { get; set; }
    public bool RestartRequested { get; set; }
    public int OutboxDepth => _outbox.Count;
    public double CpuLevel { get => _cpuLevel; set => _cpuLevel = value; }

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    // ── Metrics ─────────────────────────────────────────────────────────────

    /// <summary>Takes one CPU and RAM sample (the agent samples every 5 s by default).</summary>
    public void Sample(DateTime at)
    {
        _cpuLevel = Math.Clamp(_cpuLevel + ((_rng.NextDouble() - 0.5) * 6), 1, 100);
        _ramLevel = Math.Clamp(_ramLevel + ((_rng.NextDouble() - 0.5) * 2), 5, 99);
        _cpu.Add(new CpuMetricDto(Stamp(at), Math.Round(_cpuLevel, 1)));
        const double totalMb = 16384;
        var available = Math.Round(totalMb * (1 - (_ramLevel / 100)), 1);
        _ram.Add(new RamMetricDto(Stamp(at), Math.Round(_ramLevel, 1), available, totalMb));
    }

    /// <summary>
    /// Builds a batch exactly like OutboxDispatchWorker: new CPU/RAM samples (at most
    /// MaxBatchSize each) plus the latest snapshot of everything else, and the agent object.
    /// </summary>
    public MetricsBatchDto BuildBatch(DateTime at)
    {
        var cpu = _cpu.Take(settings.MaxBatchSize).ToList();
        var ram = _ram.Take(settings.MaxBatchSize).ToList();
        _cpu.RemoveRange(0, cpu.Count);
        _ram.RemoveRange(0, ram.Count);

        var snap = Stamp(at.AddSeconds(-1));
        // D:\ while not ready keeps its last reading, like the agent; every snapshot entry gets
        // isStale from the agent's own windows (SnapshotFreshness, with the default intervals).
        if (_driveDReady || _lastDriveD is null)
            _lastDriveD = new DiskMetricDto(snap, @"D:\", 512, 301.7, 41.1);
        var polled = SnapshotFreshness.PolledWindow(60);   // Monitoring:Disk:PollIntervalSeconds
        var polled30 = SnapshotFreshness.PolledWindow(30); // network, services, pools
        bool Stale(string timestamp, TimeSpan window) => SnapshotFreshness.IsStale(ParseStamp(timestamp), at, window);
        return new MetricsBatchDto(
            options.AgentId,
            Guid.NewGuid().ToString(),
            Stamp(at),
            cpu,
            ram,
            [
                new DiskMetricDto(snap, @"C:\", 126.4, 40.2, 68.2, Stale(snap, polled)),
                _lastDriveD with { IsStale = Stale(_lastDriveD.Timestamp, polled) },
            ],
            [new DiskQueueMetricDto(snap, "0 C:", Math.Round(_rng.NextDouble(), 2), Stale(snap, polled))],
            [new NetworkMetricDto(snap, "Intel[R] 82574L Gigabit Network Connection", 0, Stale(snap, polled30))],
            _services.Select(s => new ServiceStatusDto(snap, s.Key, DisplayName(s.Key), s.Value.Status, s.Value.Running, Stale(snap, polled30))).ToList(),
            _pools.Select(p => new ApplicationPoolStatusDto(snap, p.Key, p.Value.Status, p.Value.Running, Stale(snap, polled30))).ToList(),
            Telemetry(at),
            Endpoints(snap)?.Select(e => e with { IsStale = Stale(e.Timestamp, SnapshotFreshness.EndpointWindow(60, 10)) }).ToList());
    }

    /// <summary>
    /// Makes the simulated fixed drive D:\ not ready (false) or ready again (true). While it is not
    /// ready it stays listed with its last reading and timestamp, and turns stale after 10 minutes.
    /// </summary>
    public void SetDriveDReady(bool ready) => _driveDReady = ready;

    private static DateTime ParseStamp(string stamp) =>
        DateTime.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// The simulated endpoint check "shop-home". While its configuration is invalid it does not
    /// run, and its entry keeps the last result marked ConfigurationError, as on the agent; a
    /// check that never ran has no entry (and no endpointStatuses at all, like the agent).
    /// </summary>
    private List<EndpointStatusDto>? Endpoints(string snap)
    {
        if (_endpointConfigError is { } error)
            return _lastEndpoint is { } last ? [MarkConfigurationError(last, error)] : null;

        _lastEndpoint = new EndpointStatusDto(snap, "shop-home", "Http", "https://shop.example.com/", "Up", true,
            Math.Round(120 + (_rng.NextDouble() * 80)), 61.4, "HTTP 200");
        return [_lastEndpoint];
    }

    /// <summary>
    /// Makes the simulated check's configuration invalid (<paramref name="error"/>, in the agent's
    /// validation wording) or valid again (null): it then runs at once and reports a new result.
    /// </summary>
    public void SetEndpointConfigurationError(string? error) => _endpointConfigError = error;

    /// <summary>
    /// The agent's entry for a configured but invalid check (EndpointMonitorWorker): the last
    /// result unchanged (timestamp, type, target, isUp, measurements), status ConfigurationError
    /// and detail "Configuration error: &lt;error&gt; Last result: &lt;status&gt;[: &lt;detail&gt;]".
    /// </summary>
    public static EndpointStatusDto MarkConfigurationError(EndpointStatusDto last, string error) => last with
    {
        Status = EndpointStatus.ConfigurationError,
        Detail = $"Configuration error: {error} Last result: {last.Status}" +
                 (string.IsNullOrEmpty(last.Detail) ? string.Empty : $": {last.Detail}"),
    };

    public AgentTelemetryDto Telemetry(DateTime at)
    {
        var maintenance = Config?.Maintenance is { UntilUtc: { } until } m &&
                          WatchTower.Services.Alerting.MaintenanceState.TryParseUntil(until, out var end)
            ? new MaintenanceTelemetryDto(end > at, WatchTower.Services.Alerting.MaintenanceState.Format(end), m.Reason?.Trim())
            : new MaintenanceTelemetryDto(false, null, null);
        return new AgentTelemetryDto(
            Version,
            settings.MachineName,
            "Microsoft Windows 10.0.20348 (simulated)",
            StartedAt.ToString("O", CultureInfo.InvariantCulture),
            (long)(at - StartedAt.UtcDateTime).TotalSeconds,
            UpdatePhase,
            settings.AvailableVersion,
            null,
            maintenance);
    }

    public void EnqueueBatch(DateTime at) => Enqueue(BuildBatch(at), 0, at);

    public void EnqueueBatch(MetricsBatchDto batch, DateTime at) => Enqueue(batch, 0, at);

    // ── Events ──────────────────────────────────────────────────────────────

    public AgentEventDto EnqueueEvent(IncidentType type, string description, DateTime at,
        string? alertKey = null, bool? resolved = null, List<ProcessInfoDto>? topProcesses = null)
    {
        var evt = new AgentEventDto(Guid.NewGuid().ToString(), Stamp(at), (int)type, description, topProcesses, alertKey, resolved);
        Enqueue(evt, 1, at);
        return evt;
    }

    public List<ProcessInfoDto> TopProcesses() =>
    [
        new("w3wp", 4412, 1890.2, 812.5, Math.Round(_cpuLevel * 0.7, 1)),
        new("sqlservr", 2280, 9120.7, 4096.0, Math.Round(_cpuLevel * 0.2, 1)),
    ];

    /// <summary>Changes a simulated service's state (used by scenarios and service commands).</summary>
    public void SetService(string name, bool running) =>
        _services[name] = running ? ("Running", true) : ("Stopped", false);

    // ── Outbox (OutboxDispatchWorker) ───────────────────────────────────────

    private void Enqueue(object payload, int priority, DateTime createdAt) =>
        _outbox.Add(new OutboxItem { Id = ++_nextId, Payload = payload, Priority = priority, CreatedAt = createdAt });

    /// <summary>
    /// Sends due messages, events first then oldest first. The first failure ends the cycle and
    /// only that message counts an attempt (the portal is probably down). Metrics batches back off
    /// linearly and are dropped after MaxRetryAttempts; events back off exponentially and are kept.
    /// </summary>
    public async Task<FlushReport> FlushAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var due = _outbox.Where(m => m.NextRetryAt is null || m.NextRetryAt <= now)
            .OrderByDescending(m => m.Priority).ThenBy(m => m.Id).Take(settings.MaxBatchSize).ToList();
        int sent = 0, dropped = 0;
        foreach (var message in due)
        {
            var ok = message.Payload switch
            {
                MetricsBatchDto batch => await portal.SendMetricsBatchAsync(batch, ct),
                AgentEventDto evt => await portal.SendEventAsync(evt, ct),
                _ => true,
            };
            if (ok)
            {
                _outbox.Remove(message);
                sent++;
                continue;
            }

            message.Attempts++;
            if (message.IsEvent)
            {
                var delay = Math.Min(settings.EventRetryBaseSeconds * Math.Pow(2, message.Attempts - 1), settings.EventRetryMaxSeconds);
                message.NextRetryAt = now.AddSeconds(delay);
            }
            else if (message.Attempts >= settings.MaxRetryAttempts)
            {
                _outbox.Remove(message);
                dropped++;
                log.LogError("Dropping metrics batch {BatchId} after {Attempts} failed attempts",
                    ((MetricsBatchDto)message.Payload).BatchId, message.Attempts);
            }
            else
            {
                message.NextRetryAt = now.AddSeconds(settings.RetryBackoffSeconds * message.Attempts);
            }
            log.LogWarning("Portal send failed; {Remaining} message(s) stay queued", _outbox.Count);
            return new FlushReport(sent, dropped, true, _outbox.Count);
        }
        return new FlushReport(sent, dropped, false, _outbox.Count);
    }

    // ── Commands (CommandPollingWorker) ─────────────────────────────────────

    /// <summary>Poll, acknowledge, execute (or defer) and report, then retry unsent results.</summary>
    public async Task<int> PollCommandsAsync(CancellationToken ct)
    {
        var commands = await portal.PollCommandsAsync(ct);
        foreach (var received in commands)
        {
            if (string.IsNullOrWhiteSpace(received.CommandId))
            {
                log.LogWarning("Ignoring command without a CommandId (ActionType={ActionType})", received.ActionType);
                continue;
            }
            var command = received.Parameters is null ? received with { Parameters = [] } : received;

            if (!IsFresh(command, out var reason))
            {
                // Like the agent: not executed, answered with a failed result giving the reason.
                log.LogWarning("Rejecting command {CommandId}: {Reason}", command.CommandId, reason);
                if (_seenCommands.Add(command.CommandId))
                    await ReportAsync(new CommandResultDto(command.CommandId, false, $"Rejected: {reason}", Stamp(UtcNow)), ct);
                continue;
            }

            var isNew = _seenCommands.Add(command.CommandId);
            await portal.AcknowledgeCommandAsync(new AcknowledgeCommandDto(command.CommandId, Stamp(UtcNow)), ct);
            if (!isNew)
            {
                log.LogInformation("Command {CommandId} was delivered again; already handled", command.CommandId);
                continue;
            }

            if (TryParseUtc(command.ScheduledAt, out var scheduled) && scheduled > UtcNow.AddSeconds(10))
            {
                log.LogInformation("Command {CommandId} scheduled for {At:O}, deferring", command.CommandId, scheduled);
                _scheduled.Add(command);
                continue;
            }
            await ExecuteAsync(command, ct);
        }

        foreach (var due in _scheduled.Where(c => TryParseUtc(c.ScheduledAt, out var at) && at <= UtcNow).ToList())
        {
            _scheduled.Remove(due);
            await ExecuteAsync(due, ct);
        }

        await RetryUnsentResultsAsync(ct);
        return commands.Count;
    }

    public int UnsentResults => _unsentResults.Count;

    private async Task ExecuteAsync(AgentCommandDto command, CancellationToken ct) =>
        await ReportAsync(Run(command), ct);

    private async Task ReportAsync(CommandResultDto result, CancellationToken ct)
    {
        if (await portal.ReportCommandResultAsync(result, ct))
            return;
        var pending = new PendingResult { Result = result, ProducedAt = UtcNow };
        ScheduleNextAttempt(pending);
        _unsentResults.Add(pending);
    }

    /// <summary>
    /// Like the agent: each refused result waits out its own backoff (agent: 1, 2, 4, 8, 16, then
    /// every 30 minutes), at most MaxResultsPerCycle due results are sent per cycle, the
    /// longest-due first, and a result still refused after ResultRetryMaxSeconds is given up on.
    /// </summary>
    private async Task RetryUnsentResultsAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var due = _unsentResults.Where(r => r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt)
            .Take(settings.MaxResultsPerCycle)
            .ToList();
        foreach (var pending in due)
        {
            if (await portal.ReportCommandResultAsync(pending.Result, ct))
                _unsentResults.Remove(pending);
            else
                ScheduleNextAttempt(pending);
        }

        foreach (var expired in _unsentResults.Where(r => UtcNow - r.ProducedAt > TimeSpan.FromSeconds(settings.ResultRetryMaxSeconds)).ToList())
        {
            _unsentResults.Remove(expired);
            log.LogWarning(
                "Gave up delivering the result of command {CommandId}: the portal did not accept it in {Attempts} attempt(s) over {Seconds} s",
                expired.Result.CommandId, expired.Attempts, settings.ResultRetryMaxSeconds);
        }
    }

    private void ScheduleNextAttempt(PendingResult pending)
    {
        pending.Attempts++;
        var delay = settings.ResultRetryBaseSeconds * Math.Pow(2, Math.Min(pending.Attempts - 1, 20));
        pending.NextAttemptAt = UtcNow.AddSeconds(Math.Min(delay, settings.ResultRetryMaxDelaySeconds));
    }

    /// <summary>Authorizes and executes a command; returns the result to report.</summary>
    public CommandResultDto Run(AgentCommandDto command)
    {
        var executedAt = Stamp(UtcNow);
        var (success, message) = Authorize(command) is { } refusal ? (false, $"Not authorized: {refusal}") : Execute(command);
        log.LogInformation("Command {CommandId} ({Action}) {Outcome}: {Message}", command.CommandId,
            Enum.IsDefined(typeof(ActionType), command.ActionType) ? ((ActionType)command.ActionType).ToString() : command.ActionType.ToString(CultureInfo.InvariantCulture),
            success ? "succeeded" : "failed", message);
        return new CommandResultDto(command.CommandId, success, message, executedAt);
    }

    /// <summary>The agent's allow-list check: service/pool commands only for locally watched names.</summary>
    private string? Authorize(AgentCommandDto command)
    {
        switch ((ActionType)command.ActionType)
        {
            case ActionType.ServiceStart or ActionType.ServiceStop or ActionType.ServiceRestart:
                if (command.Parameters.GetValueOrDefault("ServiceName") is { } svc &&
                    string.Equals(svc.Trim(), "WatchTower", StringComparison.OrdinalIgnoreCase))
                    return "WatchTower does not stop or restart its own service through service commands; use AgentRestart";
                // The allow-list is the LOCAL list; a portal-pushed list only counts with
                // VisibilityPortal:CommandPoll:AllowPortalManagedTargets=true on the agent.
                return Allowed(command, "ServiceName",
                    settings.AllowPortalManagedTargets ? [.. _services.Keys] : settings.WatchedServices,
                    "locally watched service");
            case ActionType.AppPoolStart or ActionType.AppPoolStop or ActionType.AppPoolRestart or ActionType.AppPoolRecycle:
                return Allowed(command, "AppPoolName",
                    settings.AllowPortalManagedTargets ? [.. _pools.Keys] : settings.WatchedPools,
                    "locally watched application pool");
            default:
                return null;
        }
    }

    private static string? Allowed(AgentCommandDto command, string key, List<string> allowed, string label)
    {
        if (!command.Parameters.TryGetValue(key, out var target) || string.IsNullOrWhiteSpace(target))
            return $"missing {key}";
        target = target.Trim();
        return allowed.Any(a => string.Equals(a.Trim(), target, StringComparison.OrdinalIgnoreCase))
            ? null : $"'{target}' is not a {label}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\. ]{1,260}$")]
    private static partial Regex NamePattern();

    /// <summary>Simulated action handlers, with the agent's result messages.</summary>
    private (bool, string) Execute(AgentCommandDto command)
    {
        var p = command.Parameters;
        switch (command.ActionType)
        {
            case (int)ActionType.ServiceStart or (int)ActionType.ServiceStop or (int)ActionType.ServiceRestart:
                {
                    if (!p.TryGetValue("ServiceName", out var raw) || !NamePattern().IsMatch(raw))
                        return (false, "Missing or invalid ServiceName parameter");
                    var name = raw.Trim();
                    if (command.ActionType == (int)ActionType.ServiceStart && _services.GetValueOrDefault(name).Running)
                        return (true, $"Service '{name}' is already running");
                    var stop = command.ActionType == (int)ActionType.ServiceStop;
                    SetService(name, !stop);
                    return (true, command.ActionType switch
                    {
                        (int)ActionType.ServiceStart => $"Service '{name}' started successfully",
                        (int)ActionType.ServiceStop => $"Service '{name}' stopped successfully",
                        _ => $"Service '{name}' restarted successfully",
                    });
                }
            case (int)ActionType.AppPoolStart or (int)ActionType.AppPoolStop or (int)ActionType.AppPoolRestart or (int)ActionType.AppPoolRecycle:
                {
                    if (!p.TryGetValue("AppPoolName", out var raw) || !NamePattern().IsMatch(raw))
                        return (false, "Missing or invalid AppPoolName parameter");
                    var name = raw.Trim();
                    var stop = command.ActionType == (int)ActionType.AppPoolStop;
                    _pools[name] = stop ? ("Stopped", false) : ("Started", true);
                    return (true, command.ActionType switch
                    {
                        (int)ActionType.AppPoolStart => $"App pool '{name}' started successfully",
                        (int)ActionType.AppPoolStop => $"App pool '{name}' stopped successfully",
                        (int)ActionType.AppPoolRestart => $"App pool '{name}' restarted successfully",
                        _ => $"App pool '{name}' recycled successfully",
                    });
                }
            case (int)ActionType.ForceMetricSnapshot:
                SnapshotRequested = true;
                return (true, "Metric snapshot triggered");
            case (int)ActionType.ForceConfigSync:
                ConfigSyncRequested = true;
                return (true, "Config sync triggered");
            case (int)ActionType.AgentRestart:
                RestartRequested = true;
                return (true, "WatchTower is restarting; the console will be back in a few seconds.");
            case (int)ActionType.AgentUpdate:
                if (settings.AvailableVersion is not { } available)
                    return (true, $"Already up to date ({Version}).");
                if (p.TryGetValue("Version", out var pinned) && !string.IsNullOrWhiteSpace(pinned) &&
                    !string.Equals(pinned.Trim().TrimStart('v'), available, StringComparison.OrdinalIgnoreCase))
                    return (false, $"Requested version {pinned} is not the version offered by the feed ({available}).");
                UpdatePhase = "Downloading";
                RestartRequested = true; // the install restarts the agent
                return (true, $"Downloading {available}; it installs automatically after the restart countdown.");
            case (int)ActionType.ApplyConfiguration:
                return ApplyConfigurationCommand(p);
            case (int)ActionType.SendTestNotification:
                var channel = p.GetValueOrDefault("Channel")?.Trim();
                return channel is null or "" or "Email"
                    ? (true, "Email (Email): ok")
                    : (false, $"No enabled notification channel is named '{channel}'");
            default:
                return (false, $"Unknown action type: {command.ActionType}");
        }
    }

    /// <summary>Called by the scenario after a simulated restart or update completes.</summary>
    public void CompleteRestart()
    {
        RestartRequested = false;
        StartedAt = time.GetUtcNow();
        if (UpdatePhase == "Downloading" && settings.AvailableVersion is { } v)
        {
            Version = v;
            settings.AvailableVersion = null;
            UpdatePhase = "UpToDate";
        }
    }

    /// <summary>
    /// The agent's replay protection. A command without IssuedAt is accepted with a warning; with
    /// IssuedAt it must be at most MaxCommandAgeSeconds + ClockSkewSeconds old and at most
    /// ClockSkewSeconds in the future.
    /// </summary>
    public bool IsFresh(AgentCommandDto command, out string reason)
    {
        reason = string.Empty;
        // VisibilityPortal:RequireCommandIssuedAt applies even when the age check is disabled.
        if (settings.RequireCommandIssuedAt && string.IsNullOrWhiteSpace(command.IssuedAt))
        {
            reason = "issuedAt is required";
            return false;
        }
        if (settings.MaxCommandAgeSeconds <= 0)
            return true;
        if (string.IsNullOrWhiteSpace(command.IssuedAt))
        {
            if (!_warnedMissingIssuedAt)
            {
                _warnedMissingIssuedAt = true;
                log.LogWarning("Portal sends commands without issuedAt; replay protection is reduced. Update the portal.");
            }
            return true;
        }
        if (!TryParseUtc(command.IssuedAt, out var issued))
        {
            reason = $"unparseable issuedAt '{command.IssuedAt}'";
            return false;
        }
        // The agent's wording (CommandPollingWorker.IsFresh); the exact limits are accepted.
        var now = UtcNow;
        var skewSeconds = settings.ClockSkewSeconds;
        var maxAge = settings.MaxCommandAgeSeconds;
        if (issued > now.AddSeconds(skewSeconds))
            reason = $"issuedAt {issued:O} is more than {skewSeconds}s in the future (agent time {now:O})";
        else if (now - issued > TimeSpan.FromSeconds(maxAge + skewSeconds))
            reason = $"command is stale: issuedAt {issued:O} is more than {maxAge + skewSeconds}s old " +
                     $"(max age {maxAge}s + {skewSeconds}s clock skew; agent time {now:O})";
        return reason.Length == 0;
    }

    // ── Remote configuration (RemoteConfigWorker) ───────────────────────────

    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Fetch, validate with the agent's validator, apply, then acknowledge.</summary>
    public async Task<bool> SyncConfigAsync(CancellationToken ct)
    {
        var (hasUpdate, config, version) = await portal.FetchConfigurationAsync(ConfigVersion, ct);
        var applied = false;
        if (hasUpdate && config is not null && version is not null)
        {
            if (RemoteConfigValidator.Validate(config, out var error))
            {
                Apply(config, version);
                applied = true;
            }
            else
            {
                // The agent logs, keeps its current version and sends no acknowledgement.
                log.LogWarning("Remote config version {Version} failed validation: {Error}", version, error);
            }
        }

        if (UnacknowledgedConfigVersion is { } pending && await portal.AcknowledgeConfigAsync(pending, ct))
            UnacknowledgedConfigVersion = null;
        return applied;
    }

    /// <summary>Validates with the agent's rules and applies; the version then awaits acknowledgement.</summary>
    public bool TryApply(RemoteAgentConfigDto config, string version, out string error)
    {
        if (!RemoteConfigValidator.Validate(config, out error))
            return false;
        Apply(config, version);
        return true;
    }

    public void MarkConfigAcknowledged(string version)
    {
        if (UnacknowledgedConfigVersion == version)
            UnacknowledgedConfigVersion = null;
    }

    private (bool, string) ApplyConfigurationCommand(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("ConfigJson", out var json) || string.IsNullOrWhiteSpace(json))
            return (false, "Missing ConfigJson parameter");
        RemoteAgentConfigDto? config;
        try
        {
            config = JsonSerializer.Deserialize<RemoteAgentConfigDto>(json, ConfigJsonOptions);
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid ConfigJson: {ex.Message}");
        }
        if (config is null)
            return (false, "ConfigJson deserialised to null");
        if (!RemoteConfigValidator.Validate(config, out var error))
            return (false, error);
        var version = p.TryGetValue("Version", out var v) ? v : Guid.NewGuid().ToString();
        Apply(config, version);
        return (true, $"Configuration applied (version {version})");
    }

    private void Apply(RemoteAgentConfigDto config, string version)
    {
        Config = config;
        ConfigVersion = version;
        UnacknowledgedConfigVersion = version;

        // A remote list REPLACES the monitored list (it is not merged with the local one).
        if (config.Services?.WatchedServices is { } services)
            Replace(_services, services, "Running");
        if (config.AppPools?.WatchedApplicationPools is { } pools)
            Replace(_pools, pools, "Started");
        log.LogInformation("Remote configuration updated to version {Version}: {Json}", version,
            JsonSerializer.Serialize(config, ConfigJsonOptions));
    }

    private static void Replace(Dictionary<string, (string Status, bool Running)> current, List<string> names, string upStatus)
    {
        foreach (var gone in current.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            current.Remove(gone);
        foreach (var name in names.Where(n => !current.ContainsKey(n)))
            current[name] = (upStatus, true);
    }

    /// <summary>Intervals pushed in remote config (portal section) override the scenario's.</summary>
    public TimeSpan Interval(Func<RemotePortalConfig, int?> pick, TimeSpan fallback) =>
        Config?.Portal is { } portalConfig && pick(portalConfig) is { } seconds ? TimeSpan.FromSeconds(seconds) : fallback;

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The agent's timestamp format: UTC, round-trip ("O"), Z suffix.</summary>
    public static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static bool TryParseUtc(string? value, out DateTime utc) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    private static string DisplayName(string service) => service switch
    {
        "W3SVC" => "World Wide Web Publishing Service",
        "Spooler" => "Print Spooler",
        _ => service,
    };
}