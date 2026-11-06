// MinimalPortal: a reference implementation of the WatchTower portal agent API v1.
//
// Read this file top to bottom. It contains, in order:
//   1. The wire records (what the agent sends and what it expects back).
//   2. PortalState, the in-memory storage, including deduplication, the command lifecycle and
//      remote-configuration versioning.
//   3. The agent API (/api/v1/agent/...), which is the contract the agent depends on.
//   4. A small admin API (/admin/...) used to create agents, queue commands and push config.
//      The admin API is NOT part of the agent contract; real portals build their own UI.
//
// Contract reference: spec/portal-agent-api.v1.openapi.yaml and guides/portal-api/.
// Everything is kept in memory: restart the process and all agents must be enrolled again.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WatchTower.Samples.MinimalPortal;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// 1. Wire records
//
// ASP.NET Core minimal APIs use the "web" JSON defaults: camelCase output, case-insensitive
// input, and unknown properties are ignored. Ignoring unknown properties is REQUIRED by the
// contract (newer agents add fields). Output casing matters: the agent matches property names
// case-sensitively, so "commandId" works and "CommandId" is silently ignored.
// ─────────────────────────────────────────────────────────────────────────────────────────────

public sealed record MetricsBatch(
    string? AgentId,
    string? BatchId,                    // idempotency key; absent from agents up to 1.0.18
    DateTimeOffset? BatchedAt,
    List<CpuMetric>? CpuMetrics,        // incremental: each sample is in exactly one batch
    List<RamMetric>? RamMetrics,        // incremental
    List<DiskMetric>? DiskMetrics,      // snapshots: latest reading, repeated every batch
    List<DiskQueueMetric>? DiskQueueMetrics,
    List<NetworkMetric>? NetworkMetrics,
    List<ServiceStatus>? ServiceStatuses,
    List<ApplicationPoolStatus>? ApplicationPoolStatuses,
    AgentTelemetry? Agent,              // absent from agents up to 1.0.19
    List<EndpointStatus>? EndpointStatuses,
    List<PendingAlert>? PendingAlerts = null); // agents after 1.0.21; absent = nothing pending

/// <summary>
/// A breach inside its waiting period: NOT an alert (nothing was raised or sent). Shown apart from
/// the open alerts; replaced as a whole by every newer batch.
/// </summary>
public sealed record PendingAlert(
    string AlertKey, string Severity, string Title, DateTimeOffset Since, int AlertAfterSeconds, DateTimeOffset AlertsAt);

public sealed record CpuMetric(DateTimeOffset Timestamp, double UsagePercent);
public sealed record RamMetric(DateTimeOffset Timestamp, double UsagePercent, double AvailableMb, double TotalMb);
// Snapshot entries end with IsStale: true when the agent's last reading of the resource is older
// than its freshness window (the resource is still listed but no longer read). Older agents don't
// send it: null means UNKNOWN, never fresh (see PortalState.Freshness).
public sealed record DiskMetric(DateTimeOffset Timestamp, string DriveName, double TotalGb, double FreeGb, double UsagePercent, bool? IsStale = null);
public sealed record DiskQueueMetric(DateTimeOffset Timestamp, string InstanceName, double QueueLength, bool? IsStale = null);
public sealed record NetworkMetric(DateTimeOffset Timestamp, string InterfaceName, double OutputQueueLength, bool? IsStale = null);
public sealed record ServiceStatus(DateTimeOffset Timestamp, string ServiceName, string DisplayName, string Status, bool IsRunning, bool? IsStale = null);
public sealed record ApplicationPoolStatus(DateTimeOffset Timestamp, string ApplicationPoolName, string Status, bool IsRunning, bool? IsStale = null);

public sealed record EndpointStatus(
    DateTimeOffset Timestamp, string Name, string Type, string Target, string Status, bool IsUp,
    double? ResponseTimeMs, double? CertificateDaysLeft, string? Detail, bool? IsStale = null);

public sealed record AgentTelemetry(
    string Version, string MachineName, string Os, DateTimeOffset StartedAt, long UptimeSeconds,
    string UpdatePhase, string? AvailableVersion, AgentUpdateResult? LastUpdateResult,
    MaintenanceTelemetry? Maintenance);

public sealed record AgentUpdateResult(string ToVersion, bool Success, DateTimeOffset CompletedAt);
public sealed record MaintenanceTelemetry(bool Active, DateTimeOffset? UntilUtc, string? Reason);

public sealed record AgentEvent(
    string? EventId, DateTimeOffset? Timestamp, int? IncidentType, string? Description,
    List<ProcessInfo>? TopProcesses, string? AlertKey, bool? Resolved);

public sealed record ProcessInfo(string Name, int Pid, double CpuSeconds, double MemoryMb, double CpuPercent);

/// <summary>A command as the agent receives it. Every parameter value is a string.</summary>
public sealed record AgentCommand(
    string CommandId, int ActionType, Dictionary<string, string> Parameters, string? ScheduledAt, string IssuedAt);

public sealed record AcknowledgeCommand(string? CommandId, DateTimeOffset? AcknowledgedAt);
public sealed record CommandResult(string? CommandId, bool? Success, string? Message, DateTimeOffset? ExecutedAt);
public sealed record ConfigFetchResponse(JsonObject Config, string Version);
public sealed record ConfigAcknowledge(string? Version);

// Admin API bodies (not part of the agent contract).
public sealed record CreateAgentRequest(string? AgentId, bool? RequireTls);
public sealed record CreateAgentResponse(string AgentId, string Token, string EnrollmentCode);
public sealed record QueueCommandRequest(int ActionType, Dictionary<string, string>? Parameters, DateTimeOffset? ScheduledAt);

// ─────────────────────────────────────────────────────────────────────────────────────────────
// 2. Storage
// ─────────────────────────────────────────────────────────────────────────────────────────────

public enum CommandState { Pending, Acknowledged, Succeeded, Failed, Expired }

public sealed class CommandRecord
{
    public required AgentCommand Command { get; init; }
    public CommandState State { get; set; } = CommandState.Pending;
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResultReceivedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? ResultMessage { get; set; }
}

public sealed class OpenAlert
{
    public required string AlertKey { get; init; }
    public required int IncidentType { get; set; }
    public required DateTimeOffset OpenedAt { get; set; }
    public required string Description { get; set; }

    /// <summary>
    /// Timestamp of the opening or escalation that set <see cref="IncidentType"/> and
    /// <see cref="Description"/>. An older event never overwrites them.
    /// </summary>
    public required DateTimeOffset LastTransitionAt { get; set; }
}

public sealed class AgentRecord
{
    public required string AgentId { get; init; }

    /// <summary>SHA-256 of the token. Never store bearer tokens in plain text.</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public AgentTelemetry? Telemetry { get; set; }
    public DateTimeOffset? TelemetryBatchedAt { get; set; }

    // Incremental series, keyed by timestamp so a re-delivered batch (also one without a
    // batchId, from an older agent) cannot create duplicate samples.
    public SortedDictionary<DateTimeOffset, CpuMetric> Cpu { get; } = new();
    public SortedDictionary<DateTimeOffset, RamMetric> Ram { get; } = new();

    // Snapshots: latest value per resource. Older readings never overwrite newer ones.
    public Dictionary<string, DiskMetric> Disks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DiskQueueMetric> DiskQueues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NetworkMetric> Networks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ServiceStatus> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ApplicationPoolStatus> AppPools { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EndpointStatus> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// batchedAt of the batch each snapshot entry came from, keyed "kind/name": orders entries
    /// with equal timestamps (a ConfigurationError endpoint, an isStale that turned true) and
    /// is the reference time for judging freshness when the agent sends no isStale.
    /// </summary>
    public Dictionary<string, DateTimeOffset> SnapshotBatchedAt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SeenBatchIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<string> BatchIdOrder { get; } = new();
    public int BatchesReceived { get; set; }
    public int DuplicateBatches { get; set; }

    public Dictionary<string, AgentEvent> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int DuplicateEvents { get; set; }

    /// <summary>Breaches inside their waiting period, from the newest batch (not alerts).</summary>
    public List<PendingAlert> PendingAlerts { get; set; } = [];
    public DateTimeOffset? PendingAlertsBatchedAt { get; set; }

    /// <summary>Open alerts by alertKey. Alert keys are compared case-insensitively.</summary>
    public Dictionary<string, OpenAlert> OpenAlerts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each alert key last closed, to recognise an "opened" event that arrives late.</summary>
    public Dictionary<string, DateTimeOffset> LastClosedAt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CommandRecord> Commands { get; } = new(StringComparer.Ordinal);

    public JsonObject? Config { get; set; }
    public string? ConfigVersion { get; set; }
    public int ConfigRevision { get; set; }
    public string? AcknowledgedConfigVersion { get; set; }
}

public sealed class PortalOptions
{
    /// <summary>The URL agents use to reach this portal; goes into enrollment codes.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";

    /// <summary>Key for the /admin API (header X-Admin-Key).</summary>
    public string AdminKey { get; set; } = PortalState.NewSecret();

    /// <summary>
    /// Pending commands older than this are expired instead of delivered. Keep it below the
    /// agent's CommandPoll:MaxCommandAgeSeconds (default 3600 s): the agent rejects older commands
    /// without acknowledging or running them, and reports a failed result "Rejected: command is
    /// stale: ...".
    /// </summary>
    public TimeSpan CommandExpiry { get; set; } = TimeSpan.FromMinutes(50);

    /// <summary>Upper bound on stored samples, events and batch ids per agent (memory safety).</summary>
    public int MaxItemsPerAgent { get; set; } = 10_000;

    /// <summary>
    /// Accept request bodies with <c>Content-Encoding: gzip</c> or <c>br</c> (agents send them with
    /// <c>VisibilityPortal:CompressRequests=Gzip</c>) and say so in enrollment codes
    /// (<c>acceptsGzip</c>). False answers every compressed request 415, like a portal without
    /// support, which makes the agent fall back to uncompressed requests.
    /// </summary>
    public bool AcceptCompressedRequests { get; set; } = true;

    /// <summary>
    /// Largest request body after decompression; a larger one is refused with 413 before it is
    /// parsed (a small compressed body can inflate to gigabytes). Same as the uncompressed limit.
    /// </summary>
    public long MaxDecompressedBodyBytes { get; set; } = MinimalPortalApp.MaxRequestBodyBytes;
}

/// <summary>
/// In-memory store. One lock guards everything: simple and correct for a sample. A real portal
/// uses a database with unique constraints on (agentId, batchId), (agentId, eventId) and
/// (agentId, commandId) to get the same idempotency.
/// </summary>
public sealed class PortalState(PortalOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, AgentRecord> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public PortalOptions Options => options;

    public DateTimeOffset Now => time.GetUtcNow();

    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    // ── Agents and enrollment ───────────────────────────────────────────────

    /// <summary>
    /// Creates an agent with a fresh random token and returns the enrollment code to paste on
    /// the server. The token is shown once and only its hash is kept.
    /// </summary>
    public CreateAgentResponse CreateAgent(string? agentId = null, bool? requireTls = null)
    {
        agentId = string.IsNullOrWhiteSpace(agentId) ? $"agent-{Guid.NewGuid():N}"[..14] : agentId.Trim();
        var token = NewSecret();
        if (!_agents.TryAdd(agentId, new AgentRecord { AgentId = agentId, TokenHash = Hash(token) }))
            throw new InvalidOperationException($"Agent '{agentId}' already exists.");
        return new CreateAgentResponse(agentId, token, EnrollmentCode(agentId, token, requireTls));
    }

    /// <summary>Replaces the token (rotation). The old token stops working immediately.</summary>
    public CreateAgentResponse RotateToken(string agentId)
    {
        var agent = Find(agentId) ?? throw new KeyNotFoundException(agentId);
        var token = NewSecret();
        lock (_gate)
            agent.TokenHash = Hash(token);
        return new CreateAgentResponse(agentId, token, EnrollmentCode(agentId, token, null));
    }

    /// <summary>Revokes an agent: every later request with its token gets 401.</summary>
    public bool RemoveAgent(string agentId) => _agents.TryRemove(agentId, out _);

    /// <summary>
    /// "WT1." + base64url (RFC 4648 §5, no padding) of the UTF-8 JSON
    /// {"v":1,"url":…,"agentId":…,"token":…,"requireTls":…,"acceptsGzip":true}. acceptsGzip is
    /// optional (only when this portal accepts compressed requests); agents that know it turn on
    /// VisibilityPortal:CompressRequests, older ones ignore it.
    /// </summary>
    public string EnrollmentCode(string agentId, string token, bool? requireTls)
    {
        var url = options.PublicUrl.TrimEnd('/');
        var json = new JsonObject
        {
            ["v"] = 1,
            ["url"] = url,
            ["agentId"] = agentId,
            ["token"] = token,
            ["requireTls"] = requireTls ?? url.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
        };
        if (options.AcceptCompressedRequests)
            json["acceptsGzip"] = true;
        return "WT1." + Base64Url(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    public AgentRecord? Find(string agentId) => _agents.GetValueOrDefault(agentId);

    public IReadOnlyList<AgentRecord> Agents => _agents.Values.OrderBy(a => a.AgentId).ToList();

    /// <summary>
    /// Authenticates the X-Agent-Id / Bearer pair. Looks the agent up by ID, then compares token
    /// hashes in constant time so response timing does not leak how much of a token matched.
    /// </summary>
    public AgentRecord? Authenticate(string? agentId, string? token)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrEmpty(token))
            return null;
        var agent = Find(agentId);
        if (agent is null)
            return null;
        byte[] expected;
        lock (_gate)
            expected = agent.TokenHash;
        return CryptographicOperations.FixedTimeEquals(expected, Hash(token)) ? agent : null;
    }

    // ── Telemetry ───────────────────────────────────────────────────────────

    /// <summary>Stores a batch. Returns false when this batchId was already stored (duplicate).</summary>
    public bool IngestBatch(AgentRecord agent, MetricsBatch batch)
    {
        lock (_gate)
        {
            agent.LastSeenAt = Now;

            // Idempotency: the agent retries a batch until it gets a 2xx, so the same batch can
            // arrive several times (a 2xx lost on the way back). Same batchId = same result.
            if (!string.IsNullOrEmpty(batch.BatchId))
            {
                if (!agent.SeenBatchIds.Add(batch.BatchId))
                {
                    agent.DuplicateBatches++;
                    return false;
                }
                agent.BatchIdOrder.Enqueue(batch.BatchId);
                while (agent.BatchIdOrder.Count > options.MaxItemsPerAgent)
                    agent.SeenBatchIds.Remove(agent.BatchIdOrder.Dequeue());
            }
            agent.BatchesReceived++;

            foreach (var m in batch.CpuMetrics ?? [])
                agent.Cpu[m.Timestamp] = m;
            foreach (var m in batch.RamMetrics ?? [])
                agent.Ram[m.Timestamp] = m;
            Trim(agent.Cpu);
            Trim(agent.Ram);

            // Snapshots can be older than what is stored (a batch delayed in the agent's outbox
            // while the portal was down is delivered after newer ones): keep the newest.
            var batchedAt = batch.BatchedAt ?? DateTimeOffset.MinValue;
            Latest(agent, DiskKind, agent.Disks, batch.DiskMetrics, m => m.DriveName, m => m.Timestamp, batchedAt);
            Latest(agent, DiskQueueKind, agent.DiskQueues, batch.DiskQueueMetrics, m => m.InstanceName, m => m.Timestamp, batchedAt);
            Latest(agent, NetworkKind, agent.Networks, batch.NetworkMetrics, m => m.InterfaceName, m => m.Timestamp, batchedAt);
            Latest(agent, ServiceKind, agent.Services, batch.ServiceStatuses, m => m.ServiceName, m => m.Timestamp, batchedAt);
            Latest(agent, AppPoolKind, agent.AppPools, batch.ApplicationPoolStatuses, m => m.ApplicationPoolName, m => m.Timestamp, batchedAt);
            Latest(agent, EndpointKind, agent.Endpoints, batch.EndpointStatuses, m => m.Name, m => m.Timestamp, batchedAt);

            // Pending breaches are a snapshot of the whole set: the newest batch replaces it (an
            // older agent sends none, which correctly means nothing is pending).
            if (agent.PendingAlertsBatchedAt is null || agent.PendingAlertsBatchedAt <= batch.BatchedAt)
            {
                agent.PendingAlerts = batch.PendingAlerts ?? [];
                agent.PendingAlertsBatchedAt = batch.BatchedAt;
            }

            // Same for the agent object: a late, older batch must not roll the version back.
            if (batch.Agent is not null && (agent.TelemetryBatchedAt is null || agent.TelemetryBatchedAt <= batch.BatchedAt))
            {
                agent.Telemetry = batch.Agent;
                agent.TelemetryBatchedAt = batch.BatchedAt;
            }
            return true;
        }
    }

    /// <summary>Stores an event and updates the alert it belongs to. False for a duplicate eventId.</summary>
    public bool IngestEvent(AgentRecord agent, AgentEvent evt)
    {
        lock (_gate)
        {
            agent.LastSeenAt = Now;
            if (!agent.Events.TryAdd(evt.EventId!, evt))
            {
                agent.DuplicateEvents++;
                return false;
            }
            if (agent.Events.Count > options.MaxItemsPerAgent)
            {
                var oldest = agent.Events.Values.OrderBy(e => e.Timestamp).First();
                agent.Events.Remove(oldest.EventId!);
            }

            // Alert lifecycle: resolved=false opens or escalates the alert, resolved=true closes
            // it, and no "resolved" at all is a one-shot event (alertKey "reboot") or an incident
            // outside any alert (no alertKey). Events can arrive out of order (one in retry
            // backoff is overtaken by the next), so decide by timestamp, not by arrival.
            //
            //  * An opening or escalation older than the current state (the last close, or the
            //    last transition of the open alert) is kept as history only.
            //  * A close only ends the alert when it is newer than the alert's last transition.
            //  * A close and an opening with the SAME timestamp mean "closed, then opened again"
            //    in whichever order they arrive: the agent emits both at one instant when a
            //    resource changes from one problem to another (an endpoint going from Down to
            //    Expiring sends EndpointRecovered and CertificateExpiring together).
            //
            // Timestamps cannot settle every reordering (two changes in the same instant, or a
            // transition lost because the agent dropped it): reconcile the open alerts against
            // the snapshots in the metrics batches (endpointStatuses, serviceStatuses, ...).
            if (evt.AlertKey is { Length: > 0 } key && evt.Resolved is { } resolved)
            {
                var at = evt.Timestamp!.Value;
                if (!resolved)
                {
                    if (agent.OpenAlerts.TryGetValue(key, out var open))
                    {
                        if (at < open.LastTransitionAt)
                            return true; // older than the current state: history only

                        open.IncidentType = evt.IncidentType!.Value; // escalation keeps the alert open
                        open.Description = evt.Description!;
                        open.LastTransitionAt = at;
                    }
                    else
                    {
                        if (agent.LastClosedAt.TryGetValue(key, out var closedAt) && closedAt > at)
                            return true; // already closed later than this open: keep it as history only

                        agent.OpenAlerts[key] = new OpenAlert
                        {
                            AlertKey = key,
                            IncidentType = evt.IncidentType!.Value,
                            OpenedAt = at,
                            Description = evt.Description!,
                            LastTransitionAt = at,
                        };
                    }
                }
                else
                {
                    if (agent.OpenAlerts.TryGetValue(key, out var open))
                    {
                        if (open.LastTransitionAt < at)
                            agent.OpenAlerts.Remove(key);
                        else if (open.OpenedAt <= at)
                            open.OpenedAt = open.LastTransitionAt; // reopened at or after this close
                    }
                    if (!agent.LastClosedAt.TryGetValue(key, out var previous) || previous < at)
                        agent.LastClosedAt[key] = at;
                }
            }
            return true;
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    public AgentCommand QueueCommand(AgentRecord agent, int actionType, Dictionary<string, string>? parameters,
        DateTimeOffset? scheduledAt)
    {
        var command = new AgentCommand(
            Guid.NewGuid().ToString(),                       // globally unique, never reused: the agent never runs a known id again
            actionType,
            parameters ?? new Dictionary<string, string>(),  // values are always strings
            scheduledAt?.UtcDateTime.ToString("O"),
            Now.UtcDateTime.ToString("O"));                  // always sent: agents with RequireCommandIssuedAt reject commands without it
        lock (_gate)
            agent.Commands[command.CommandId] = new CommandRecord { Command = command };
        return command;
    }

    /// <summary>
    /// Commands that are still waiting for an acknowledgement. They are returned on every poll
    /// until acknowledged: the agent deduplicates by commandId, so re-delivery is safe and covers
    /// a lost response. Commands too old for the agent to accept are expired here instead.
    /// </summary>
    public IReadOnlyList<AgentCommand> PendingCommands(AgentRecord agent)
    {
        lock (_gate)
        {
            agent.LastSeenAt = Now;
            var pending = new List<AgentCommand>();
            foreach (var record in agent.Commands.Values.OrderBy(r => r.Command.IssuedAt, StringComparer.Ordinal))
            {
                if (record.State != CommandState.Pending)
                    continue;
                if (Now - DateTimeOffset.Parse(record.Command.IssuedAt, System.Globalization.CultureInfo.InvariantCulture) > options.CommandExpiry)
                {
                    record.State = CommandState.Expired;
                    record.ResultMessage = "Expired before the agent picked it up";
                    continue;
                }
                pending.Add(record.Command);
            }
            return pending;
        }
    }

    public void Acknowledge(AgentRecord agent, string commandId, DateTimeOffset at)
    {
        lock (_gate)
        {
            // An acknowledgement for an unknown command (e.g. this in-memory portal restarted)
            // is harmless: accept it so the agent has nothing to retry.
            if (agent.Commands.TryGetValue(commandId, out var record) && record.State == CommandState.Pending)
            {
                record.State = CommandState.Acknowledged;
                record.AcknowledgedAt = at;
            }
        }
    }

    /// <summary>
    /// Records a result. The agent retries results until it gets a 2xx, so duplicates are
    /// normal: the first result wins. A result may also arrive without any acknowledgement: the
    /// agent does not retry acknowledgements, and it never acknowledges a command it rejects for
    /// its issuedAt (that result says "Rejected: ..."). AcknowledgedAt stays as received.
    /// </summary>
    public void RecordResult(AgentRecord agent, string commandId, bool success, string message, DateTimeOffset executedAt)
    {
        lock (_gate)
        {
            if (!agent.Commands.TryGetValue(commandId, out var record) || record.ResultReceivedAt is not null)
                return;
            // A result can be late, even for a command this portal already expired: after an agent
            // restart a stored command runs (or is "Rejected: command expired before it could run
            // (agent restarted)"), and one cut short is reported "Interrupted: ...". The first result wins.
            record.State = success ? CommandState.Succeeded : CommandState.Failed;
            record.ResultMessage = message;
            record.ExecutedAt = executedAt;
            record.ResultReceivedAt = Now;
        }
    }

    // ── Remote configuration ────────────────────────────────────────────────

    /// <summary>
    /// Stores a new configuration version. The object replaces the previous one as a whole: a
    /// section left out falls back to the agent's local settings.
    /// </summary>
    public string SetConfig(AgentRecord agent, JsonObject config)
    {
        lock (_gate)
        {
            agent.ConfigRevision++;
            agent.Config = config;
            agent.ConfigVersion = $"{agent.AgentId}.{agent.ConfigRevision}";
            return agent.ConfigVersion;
        }
    }

    /// <summary>The configuration to send, or null (answer 204) when the agent already has it.</summary>
    public ConfigFetchResponse? ConfigFor(AgentRecord agent, string? agentVersion)
    {
        lock (_gate)
        {
            agent.LastSeenAt = Now;
            if (agent.Config is null || agent.ConfigVersion is null || agent.ConfigVersion == agentVersion)
                return null;
            return new ConfigFetchResponse((JsonObject)agent.Config.DeepClone(), agent.ConfigVersion);
        }
    }

    public void AcknowledgeConfig(AgentRecord agent, string version)
    {
        lock (_gate)
            agent.AcknowledgedConfigVersion = version;
    }

    /// <summary>A consistent copy of an agent's state for display (admin API, tests).</summary>
    public JsonObject Describe(AgentRecord agent)
    {
        lock (_gate)
        {
            return JsonSerializer.SerializeToNode(new
            {
                agent.AgentId,
                agent.CreatedAt,
                agent.LastSeenAt,
                agent.Telemetry,
                agent.BatchesReceived,
                agent.DuplicateBatches,
                CpuSamples = agent.Cpu.Count,
                LatestCpu = agent.Cpu.Values.LastOrDefault(),
                RamSamples = agent.Ram.Count,
                LatestRam = agent.Ram.Values.LastOrDefault(),
                // Every snapshot entry as received, plus "freshness": how a console shows it.
                Disks = SnapshotView(agent, DiskKind, agent.Disks.Values, m => m.DriveName, m => m.Timestamp, m => m.IsStale),
                DiskQueues = SnapshotView(agent, DiskQueueKind, agent.DiskQueues.Values, m => m.InstanceName, m => m.Timestamp, m => m.IsStale),
                Networks = SnapshotView(agent, NetworkKind, agent.Networks.Values, m => m.InterfaceName, m => m.Timestamp, m => m.IsStale),
                Services = SnapshotView(agent, ServiceKind, agent.Services.Values, m => m.ServiceName, m => m.Timestamp, m => m.IsStale),
                AppPools = SnapshotView(agent, AppPoolKind, agent.AppPools.Values, m => m.ApplicationPoolName, m => m.Timestamp, m => m.IsStale),
                Endpoints = SnapshotView(agent, EndpointKind, agent.Endpoints.Values, e => e.Name, e => e.Timestamp, e => e.IsStale,
                    (e, view) => view["health"] = EndpointHealth(e)),
                Events = agent.Events.Values.OrderByDescending(e => e.Timestamp).Take(50),
                agent.DuplicateEvents,
                OpenAlerts = agent.OpenAlerts.Values,
                // Waiting out their alert's waiting period: not alerts, never counted as open.
                agent.PendingAlerts,
                Commands = agent.Commands.Values.Select(r => new
                {
                    r.Command,
                    State = r.State.ToString(),
                    r.AcknowledgedAt,
                    r.ExecutedAt,
                    r.ResultMessage,
                }),
                agent.ConfigVersion,
                agent.AcknowledgedConfigVersion,
                agent.Config,
            }, PortalApi.Json)!.AsObject();
        }
    }

    private void Trim<T>(SortedDictionary<DateTimeOffset, T> series)
    {
        while (series.Count > options.MaxItemsPerAgent)
            series.Remove(series.Keys.First());
    }

    private const string DiskKind = "disk", DiskQueueKind = "diskQueue", NetworkKind = "network",
        ServiceKind = "service", AppPoolKind = "appPool", EndpointKind = "endpoint";

    /// <summary>
    /// Without isStale (older agents), an entry is judged stale when its reading is this much
    /// older than its batch: the agent's shortest freshness window. A portal that tracks the
    /// agent's poll and check intervals can apply the exact windows of the spec instead.
    /// </summary>
    public static readonly TimeSpan FallbackStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How a console shows a snapshot entry. isStale true: the agent still lists the resource but
    /// has not read it within its window, so the values are the last ones read, not current.
    /// isStale absent: unknown (an agent that predates it), so judge from the timestamp.
    /// </summary>
    public static string Freshness(DateTimeOffset timestamp, bool? isStale, DateTimeOffset batchedAt) => isStale switch
    {
        true => $"stale (no reading since {timestamp:O})",
        false => "fresh",
        null when batchedAt - timestamp > FallbackStaleAfter => $"stale (no reading since {timestamp:O}; judged from the timestamp, the agent sends no isStale)",
        null => "fresh (judged from the timestamp, the agent sends no isStale)",
    };

    private static JsonArray SnapshotView<T>(AgentRecord agent, string kind, IEnumerable<T> entries, Func<T, string> key,
        Func<T, DateTimeOffset> at, Func<T, bool?> isStale, Action<T, JsonObject>? extra = null)
    {
        var view = new JsonArray();
        foreach (var entry in entries)
        {
            var node = JsonSerializer.SerializeToNode(entry, PortalApi.Json)!.AsObject();
            extra?.Invoke(entry, node);
            node["freshness"] = Freshness(at(entry), isStale(entry),
                agent.SnapshotBatchedAt.GetValueOrDefault($"{kind}/{key(entry)}", DateTimeOffset.MinValue));
            view.Add(node);
        }
        return view;
    }

    /// <summary>
    /// How a console shows an endpoint entry. Status is an open set: show unknown values as such.
    /// ConfigurationError means "not checked": its isUp belongs to the last result, so it is
    /// neither up nor down now.
    /// </summary>
    public static string EndpointHealth(EndpointStatus entry) => entry.Status switch
    {
        "Up" => "up",
        "Expiring" => "warning",
        "Down" => "down",
        "ConfigurationError" => "not checked (configuration error)",
        _ => $"unknown ({entry.Status})",
    };

    /// <summary>
    /// Keeps the newest entry per resource. An equal timestamp is decided by the batch: the entry
    /// from the later batchedAt wins. Two cases repeat a reading's timestamp with new information:
    /// an endpoint check whose configuration became invalid (ConfigurationError, last result's
    /// timestamp unchanged) and an entry whose isStale turned true. A late re-delivery of an older
    /// batch can bring neither the old status nor "fresh" back.
    /// </summary>
    private static void Latest<T>(AgentRecord agent, string kind, Dictionary<string, T> into, List<T>? items,
        Func<T, string> key, Func<T, DateTimeOffset> at, DateTimeOffset batchedAt)
    {
        foreach (var item in items ?? [])
        {
            var batchKey = $"{kind}/{key(item)}";
            if (into.TryGetValue(key(item), out var current) &&
                (at(current) > at(item) ||
                 (at(current) == at(item) && agent.SnapshotBatchedAt.GetValueOrDefault(batchKey) > batchedAt)))
                continue;
            into[key(item)] = item;
            agent.SnapshotBatchedAt[batchKey] = batchedAt;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// 3. Agent API: the contract
// 4. Admin API: sample-only helpers
// ─────────────────────────────────────────────────────────────────────────────────────────────

public static class PortalApi
{
    /// <summary>Web defaults (camelCase, case-insensitive reads) and no nulls in output.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string AgentItem = "watchtower.agent";

    /// <summary>The request encodings this portal can read (besides identity).</summary>
    public static readonly string[] RequestEncodings = ["gzip", "br"];

    /// <summary>
    /// Compressed request bodies (opt-in on the agent: VisibilityPortal:CompressRequests=Gzip).
    /// Decodes Content-Encoding gzip or br before the body is parsed, refusing a body that
    /// inflates past <see cref="PortalOptions.MaxDecompressedBodyBytes"/> with 413 and a damaged
    /// one with 400. Any other encoding, or every one with AcceptCompressedRequests=false, gets
    /// 415 with an Accept-Encoding header (RFC 7694): that 415 is what makes an agent resend the
    /// request uncompressed and stop compressing, so a portal without support MUST answer it
    /// rather than try to parse the compressed bytes.
    /// </summary>
    public static IApplicationBuilder UseRequestDecompression(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var header = context.Request.Headers.ContentEncoding.ToString().Trim();
        if (header.Length == 0 || header.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var options = context.RequestServices.GetRequiredService<PortalOptions>();
        var encoding = header.ToLowerInvariant();
        if (!options.AcceptCompressedRequests || !RequestEncodings.Contains(encoding))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            context.Response.Headers.AcceptEncoding = options.AcceptCompressedRequests
                ? string.Join(", ", RequestEncodings)
                : "identity";
            await context.Response.WriteAsJsonAsync(new { error = $"Content-Encoding '{header}' is not supported" });
            return;
        }

        // Buffered: simple for a sample. Kestrel's MaxRequestBodySize bounds the compressed body,
        // MaxDecompressedBodyBytes the decoded one.
        var raw = new MemoryStream();
        await context.Request.Body.CopyToAsync(raw, context.RequestAborted);
        raw.Position = 0;
        var body = new MemoryStream();
        try
        {
            await using Stream decoder = encoding == "gzip"
                ? new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true)
                : new System.IO.Compression.BrotliStream(raw, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            var chunk = new byte[81_920];
            int read;
            while ((read = await decoder.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (body.Length + read > options.MaxDecompressedBodyBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = $"the decompressed body exceeds {options.MaxDecompressedBodyBytes} bytes",
                    });
                    return;
                }
                body.Write(chunk, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = $"the body is not valid {encoding} data" });
            return;
        }

        context.RequestServices.GetRequiredService<ILogger<PortalState>>().LogInformation(
            "{Path}: {Encoding} body, {Compressed} bytes on the wire, {Decompressed} decoded",
            context.Request.Path.Value, encoding, raw.Length, body.Length);
        body.Position = 0;
        context.Request.Body = body;
        context.Request.Headers.ContentEncoding = Microsoft.Extensions.Primitives.StringValues.Empty;
        context.Request.ContentLength = body.Length;
        await next(context);
    });

    public static void MapAgentApi(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/v1/agent").AddEndpointFilter(AuthenticateAgent);

        // POST metrics/batch — any 2xx means delivered; the agent ignores the body. 202 is used
        // for duplicates too: answering an error would make the agent retry a batch we have.
        agent.MapPost("/metrics/batch", (HttpContext http, PortalState state, MetricsBatch? batch, ILogger<PortalState> log) =>
        {
            if (batch is null || batch.BatchedAt is null || batch.CpuMetrics is null || batch.RamMetrics is null)
                return Results.BadRequest(new { error = "batchedAt, cpuMetrics and ramMetrics are required" });

            var a = Agent(http);
            var stored = state.IngestBatch(a, batch);
            log.LogInformation("{Agent}: metrics batch {BatchId} ({Cpu} cpu, {Ram} ram){Duplicate}", a.AgentId,
                batch.BatchId ?? "(no id)", batch.CpuMetrics.Count, batch.RamMetrics.Count, stored ? "" : " - duplicate");
            return Results.Accepted();
        });

        // POST events — one event per request; deduplicated by eventId.
        agent.MapPost("/events", (HttpContext http, PortalState state, AgentEvent? evt, ILogger<PortalState> log) =>
        {
            if (evt is null || string.IsNullOrEmpty(evt.EventId) || evt.Timestamp is null || evt.IncidentType is null ||
                evt.Description is null)
                return Results.BadRequest(new { error = "eventId, timestamp, incidentType and description are required" });

            var a = Agent(http);
            var stored = state.IngestEvent(a, evt);
            log.LogInformation("{Agent}: event {Type} {Key}{Resolved}: {Description}{Duplicate}", a.AgentId, evt.IncidentType,
                evt.AlertKey ?? "-", evt.Resolved is { } r ? (r ? " resolved" : " opened") : "", evt.Description,
                stored ? "" : " - duplicate");
            return Results.Accepted();
        });

        // GET commands/pending — 200 with a JSON array, or 204 when there is nothing.
        agent.MapGet("/commands/pending", (HttpContext http, PortalState state) =>
        {
            var pending = state.PendingCommands(Agent(http));
            return pending.Count == 0 ? Results.NoContent() : Results.Json(pending, Json);
        });

        // POST commands/{id}/acknowledge — sent once; never retried by the agent.
        agent.MapPost("/commands/{commandId}/acknowledge",
            (HttpContext http, PortalState state, string commandId, AcknowledgeCommand? ack) =>
            {
                if (ack?.CommandId is { } bodyId && bodyId != commandId)
                    return Results.BadRequest(new { error = "commandId in the body does not match the URL" });
                state.Acknowledge(Agent(http), commandId, ack?.AcknowledgedAt ?? state.Now);
                return Results.NoContent();
            });

        // POST commands/{id}/result — retried until 2xx, so duplicates (and results for commands
        // this portal no longer knows) are answered 2xx as well.
        agent.MapPost("/commands/{commandId}/result",
            (HttpContext http, PortalState state, string commandId, CommandResult? result, ILogger<PortalState> log) =>
            {
                if (result?.Success is null || result.Message is null)
                    return Results.BadRequest(new { error = "success and message are required" });
                if (result.CommandId is { } bodyId && bodyId != commandId)
                    return Results.BadRequest(new { error = "commandId in the body does not match the URL" });

                var a = Agent(http);
                state.RecordResult(a, commandId, result.Success.Value, result.Message, result.ExecutedAt ?? state.Now);
                log.LogInformation("{Agent}: command {CommandId} {Outcome}: {Message}", a.AgentId, commandId,
                    result.Success.Value ? "succeeded" : "failed", result.Message);
                return Results.NoContent();
            });

        // GET config?version=… — 204 when the agent has the current version, else 200.
        agent.MapGet("/config", (HttpContext http, PortalState state, string? version) =>
        {
            var config = state.ConfigFor(Agent(http), version);
            return config is null ? Results.NoContent() : Results.Json(config, Json);
        });

        // POST config/acknowledge — the agent applied this version.
        agent.MapPost("/config/acknowledge", (HttpContext http, PortalState state, ConfigAcknowledge? ack, ILogger<PortalState> log) =>
        {
            if (string.IsNullOrEmpty(ack?.Version))
                return Results.BadRequest(new { error = "version is required" });
            var a = Agent(http);
            state.AcknowledgeConfig(a, ack.Version);
            log.LogInformation("{Agent}: applied configuration {Version}", a.AgentId, ack.Version);
            return Results.NoContent();
        });
    }

    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
        {
            var state = context.HttpContext.RequestServices.GetRequiredService<PortalState>();
            var given = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            return CryptographicOperations.FixedTimeEquals(PortalState.Hash(given), PortalState.Hash(state.Options.AdminKey))
                ? await next(context)
                : Results.Unauthorized();
        });

        admin.MapGet("/agents", (PortalState state) =>
            state.Agents.Select(a => new { a.AgentId, a.LastSeenAt, a.Telemetry?.Version, a.Telemetry?.MachineName }));

        admin.MapPost("/agents", (PortalState state, CreateAgentRequest? request) =>
        {
            try
            {
                return Results.Ok(state.CreateAgent(request?.AgentId, request?.RequireTls));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        admin.MapGet("/agents/{agentId}", (PortalState state, string agentId) =>
            state.Find(agentId) is { } a ? Results.Json(state.Describe(a), Json) : Results.NotFound());

        admin.MapPost("/agents/{agentId}/token", (PortalState state, string agentId) =>
            state.Find(agentId) is null ? Results.NotFound() : Results.Ok(state.RotateToken(agentId)));

        admin.MapDelete("/agents/{agentId}", (PortalState state, string agentId) =>
            state.RemoveAgent(agentId) ? Results.NoContent() : Results.NotFound());

        admin.MapPost("/agents/{agentId}/commands", (PortalState state, string agentId, QueueCommandRequest request) =>
            state.Find(agentId) is { } a
                ? Results.Json(state.QueueCommand(a, request.ActionType, request.Parameters, request.ScheduledAt), Json)
                : Results.NotFound());

        // The body is the RemoteAgentConfig object itself, e.g. {"cpu":{"thresholdPercent":90}}.
        admin.MapPut("/agents/{agentId}/config", (PortalState state, string agentId, JsonObject config) =>
        {
            if (state.Find(agentId) is not { } a)
                return Results.NotFound();
            var error = RemoteConfigRules.Validate(config, state.Now);
            return error is null
                ? Results.Ok(new { version = state.SetConfig(a, config) })
                : Results.BadRequest(new { error });
        });
    }

    /// <summary>
    /// Agent authentication: X-Agent-Id plus Authorization: Bearer &lt;token&gt; on every request.
    /// 401 on failure. The agent logs 401s on POSTs as "Check AgentToken" and keeps retrying.
    /// </summary>
    private static async ValueTask<object?> AuthenticateAgent(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var state = http.RequestServices.GetRequiredService<PortalState>();
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var agent = state.Authenticate(http.Request.Headers["X-Agent-Id"].ToString(), token);
        if (agent is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Unauthorized();
        }
        http.Items[AgentItem] = agent; // identity comes from the credentials, never from the body
        return await next(context);
    }

    private static AgentRecord Agent(HttpContext http) => (AgentRecord)http.Items[AgentItem]!;
}

/// <summary>
/// The agent's own validation rules for remote configuration (RemoteConfigValidator), repeated
/// here so the portal refuses a configuration the agent would reject. A rejected configuration is
/// never acknowledged: without this check the portal would only notice the missing ack.
/// </summary>
public static class RemoteConfigRules
{
    private static readonly Dictionary<string, Dictionary<string, string>> Sections = new(StringComparer.Ordinal)
    {
        ["cpu"] = new() { ["pollIntervalSeconds"] = "interval", ["thresholdPercent"] = "percent", ["sustainedSeconds"] = "wait" },
        ["ram"] = new() { ["pollIntervalSeconds"] = "interval", ["thresholdPercent"] = "percent", ["sustainedSeconds"] = "wait" },
        ["disk"] = new()
        {
            ["pollIntervalSeconds"] = "interval",
            ["warningThresholdPercent"] = "percent",
            ["criticalThresholdPercent"] = "percent",
            ["queueLengthWarningThreshold"] = "queue",
            ["sustainedSeconds"] = "wait",
            ["queueSustainedSeconds"] = "wait",
        },
        ["network"] = new() { ["pollIntervalSeconds"] = "interval", ["outputQueueWarningThreshold"] = "queue", ["sustainedSeconds"] = "wait" },
        ["services"] = new() { ["pollIntervalSeconds"] = "interval", ["watchedServices"] = "names", ["sustainedSeconds"] = "wait" },
        ["appPools"] = new() { ["pollIntervalSeconds"] = "interval", ["watchedApplicationPools"] = "names", ["sustainedSeconds"] = "wait" },
        ["endpoints"] = new() { ["sustainedSeconds"] = "wait" },
        ["portal"] = new()
        {
            ["outboxFlushIntervalSeconds"] = "interval",
            ["commandPollIntervalSeconds"] = "interval",
            ["remoteConfigPollIntervalSeconds"] = "interval",
        },
        ["maintenance"] = new() { ["untilUtc"] = "until", ["reason"] = "reason" },
    };

    private static readonly System.Text.RegularExpressions.Regex NamePattern = new(@"^[\w .\-$@()]+$");

    /// <summary>Returns null when valid, else the first problem.</summary>
    public static string? Validate(JsonObject config, DateTimeOffset now)
    {
        foreach (var (sectionName, sectionNode) in config)
        {
            // The agent matches names case-sensitively and ignores unknown ones, so a typo such
            // as "Cpu" would be silently ignored by the agent. Refuse it here instead.
            if (!Sections.TryGetValue(sectionName, out var fields))
                return $"unknown section '{sectionName}' (names are camelCase and case-sensitive)";
            if (sectionNode is null)
                continue;
            if (sectionNode is not JsonObject section)
                return $"{sectionName} must be an object";

            foreach (var (field, value) in section)
            {
                if (!fields.TryGetValue(field, out var kind))
                    return $"unknown setting '{sectionName}.{field}'";
                if (value is null)
                    continue;
                var error = kind switch
                {
                    "interval" => Integer(value, 1, 86400),
                    "wait" => Integer(value, 0, 86400), // waiting periods: 0 = alert at once
                    "percent" => Number(value, 1, 100),
                    "queue" => Number(value, 0, 10000),
                    "names" => Names(value),
                    "until" => Until(value, now),
                    "reason" => value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length <= 500
                        ? null : "must be a string of at most 500 characters",
                    _ => null,
                };
                if (error is not null)
                    return $"{sectionName}.{field} {error}";
            }
        }

        if (config["disk"]?["warningThresholdPercent"] is JsonValue warning && config["disk"]?["criticalThresholdPercent"] is JsonValue critical &&
            warning.GetValue<double>() > critical.GetValue<double>())
            return "disk.warningThresholdPercent must be <= disk.criticalThresholdPercent";
        return null;
    }

    private static string? Integer(JsonNode value, int min, int max) =>
        value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue<int>(out var i) && i >= min && i <= max
            ? null : $"must be a whole number from {min} to {max}";

    private static string? Number(JsonNode value, double min, double max) =>
        value.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() is var d && d >= min && d <= max
            ? null : $"must be a number from {min} to {max}";

    private static string? Names(JsonNode value)
    {
        if (value is not JsonArray names)
            return "must be an array of names";
        if (names.Count > 200)
            return "must not contain more than 200 entries";
        foreach (var name in names)
        {
            if (name?.GetValueKind() != JsonValueKind.String || name.GetValue<string>() is not { } s ||
                string.IsNullOrWhiteSpace(s) || s.Length > 256 || !NamePattern.IsMatch(s))
                return "entries must be non-empty names of at most 256 letters, digits, spaces or _ . - $ @ ( )";
        }
        return null;
    }

    private static string? Until(JsonNode value, DateTimeOffset now)
    {
        if (value.GetValueKind() != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var until))
            return "must be an ISO-8601 timestamp";
        return until > now.AddDays(7).AddMinutes(5) ? "must be at most 7 days in the future" : null;
    }
}