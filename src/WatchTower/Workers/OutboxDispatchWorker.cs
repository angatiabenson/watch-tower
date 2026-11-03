using System.Text.Json;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;

namespace WatchTower.Workers;

public class OutboxDispatchWorker : BackgroundService
{
    private readonly IPortalClient _portalClient;
    private readonly IOutboxStore _outboxStore;
    private readonly IPortalSyncState _syncState;
    private readonly IMetricsStore _metricsStore;
    private readonly IMetricSnapshotTrigger _snapshotTrigger;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxDispatchWorker> _logger;
    private readonly IAgentTelemetry? _telemetry;

    internal const string MetricsMessageType = "MetricsBatch";
    internal const string EventMessageType = "AgentEvent";

    /// <summary>First retry delay for an undelivered incident event; doubles per attempt.</summary>
    internal static readonly TimeSpan EventRetryBaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for the incident event retry delay.</summary>
    internal static readonly TimeSpan EventRetryMaxDelay = TimeSpan.FromMinutes(15);

    private int _flushCycleCount;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public OutboxDispatchWorker(
        IPortalClient portalClient,
        IOutboxStore outboxStore,
        IPortalSyncState syncState,
        IMetricsStore metricsStore,
        IMetricSnapshotTrigger snapshotTrigger,
        IOptionsMonitor<PortalOptions> options,
        TimeProvider timeProvider,
        ILogger<OutboxDispatchWorker> logger,
        IAgentTelemetry? telemetry = null)
    {
        _telemetry = telemetry;
        _portalClient = portalClient;
        _outboxStore = outboxStore;
        _syncState = syncState;
        _metricsStore = metricsStore;
        _snapshotTrigger = snapshotTrigger;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Dispatch Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);

                // Sleep for the flush interval, but wake immediately when the portal sends
                // ForceMetricSnapshot so the requested snapshot is collected and pushed now.
                var flushInterval = Math.Max(1, _options.CurrentValue.Outbox.FlushIntervalSeconds);
                if (await _snapshotTrigger.WaitAsync(TimeSpan.FromSeconds(flushInterval), _timeProvider, stoppingToken))
                    _logger.LogInformation("Metric snapshot requested by the portal; collecting now");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Outbox Dispatch Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Outbox Dispatch Worker stopped");
    }

    /// <summary>One collect + flush (+ periodic purge) cycle. Exposed for deterministic testing.</summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
            return;

        await CollectAndEnqueueMetricsAsync(opts, ct);
        await FlushOutboxAsync(opts, ct);

        _flushCycleCount++;
        if (_flushCycleCount % 100 == 0)
            await _outboxStore.PurgeDeliveredAsync(TimeSpan.FromHours(opts.Outbox.PurgeDeliveredAfterHours));
    }

    private async Task CollectAndEnqueueMetricsAsync(PortalOptions opts, CancellationToken ct)
    {
        try
        {
            var maxBatch = opts.Outbox.MaxBatchSize;
            var agentId = opts.AgentId;
            var batchedAt = UtcNow.ToString("O");
            var batchId = Guid.NewGuid().ToString();

            // Cursor-based append tables
            var cpuCursor = await _syncState.GetLastForwardedRowIdAsync("cpu");
            var (cpuLastRowId, cpuMetrics) = _metricsStore.GetCpuMetricsSince(cpuCursor, maxBatch);

            var ramCursor = await _syncState.GetLastForwardedRowIdAsync("ram");
            var (ramLastRowId, ramMetrics) = _metricsStore.GetRamMetricsSince(ramCursor, maxBatch);

            // Snapshot tables (always latest state)
            var diskMetrics = _metricsStore.GetLatestDiskMetrics();
            var diskQueueMetrics = _metricsStore.GetLatestDiskQueueMetrics();
            var networkMetrics = _metricsStore.GetLatestNetworkMetrics();
            var serviceStatuses = _metricsStore.GetLatestServiceStatuses();
            var appPoolStatuses = _metricsStore.GetLatestApplicationPoolStatuses();
            var endpointStatuses = _metricsStore.GetLatestEndpointStatuses();

            bool hasData = cpuMetrics.Count > 0 || ramMetrics.Count > 0 ||
                           diskMetrics.Count > 0 || networkMetrics.Count > 0 ||
                           serviceStatuses.Count > 0 || appPoolStatuses.Count > 0 ||
                           endpointStatuses.Count > 0;

            if (!hasData)
                return;

            var batch = new MetricsBatchDto(
                agentId,
                batchId,
                batchedAt,
                cpuMetrics.Select(m => new CpuMetricDto(m.Timestamp.ToString("O"), m.UsagePercent)).ToList(),
                ramMetrics.Select(m => new RamMetricDto(m.Timestamp.ToString("O"), m.UsagePercent, m.AvailableMb, m.TotalMb)).ToList(),
                diskMetrics.Select(m => new DiskMetricDto(m.Timestamp.ToString("O"), m.DriveName, m.TotalGb, m.FreeGb, m.UsagePercent)).ToList(),
                diskQueueMetrics.Select(m => new DiskQueueMetricDto(m.Timestamp.ToString("O"), m.InstanceName, m.QueueLength)).ToList(),
                networkMetrics.Select(m => new NetworkMetricDto(m.Timestamp.ToString("O"), m.InterfaceName, m.OutputQueueLength)).ToList(),
                serviceStatuses.Select(m => new ServiceStatusDto(m.Timestamp.ToString("O"), m.ServiceName, m.DisplayName, m.Status, m.IsRunning)).ToList(),
                appPoolStatuses.Select(m => new ApplicationPoolStatusDto(m.Timestamp.ToString("O"), m.ApplicationPoolName, m.Status, m.IsRunning)).ToList(),
                _telemetry?.Snapshot(),
                endpointStatuses.Count == 0
                    ? null
                    : endpointStatuses.Select(e => new EndpointStatusDto(e.Timestamp.ToString("O"), e.Name, e.Type, e.Target,
                        e.Status, e.IsUp, e.ResponseTimeMs, e.CertificateDaysLeft, e.Detail)).ToList()
            );

            await _outboxStore.AddMessageAsync(
                MetricsMessageType,
                JsonSerializer.Serialize(batch, JsonOpts),
                priority: 0,
                cpuRowId: cpuMetrics.Count > 0 ? cpuLastRowId : null,
                ramRowId: ramMetrics.Count > 0 ? ramLastRowId : null);

            // Advance the READ cursor at enqueue so the same rows are not collected
            // again on the next cycle. The SENT cursor (cpu_sent/ram_sent), which gates
            // purging, is only advanced once the portal confirms delivery (see FlushOutboxAsync).
            if (cpuLastRowId > cpuCursor)
                await _syncState.SetLastForwardedRowIdAsync("cpu", cpuLastRowId);
            if (ramLastRowId > ramCursor)
                await _syncState.SetLastForwardedRowIdAsync("ram", ramLastRowId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect and enqueue metrics");
        }
    }


    /// <summary>
    /// Delivers pending outbox messages, highest priority first.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The first send failure ends the cycle: the portal is probably down, so the rest of
    /// the batch is left untouched (no attempt counted) instead of hammering it.</item>
    /// <item><c>AgentEvent</c> messages (incidents) never fail terminally: they back off
    /// exponentially (<see cref="EventRetryBaseDelay"/> doubling up to
    /// <see cref="EventRetryMaxDelay"/>) and are only dropped once older than
    /// <see cref="OutboxOptions.EventMaxAgeHours"/>.</item>
    /// <item><c>MetricsBatch</c> keeps its bounded retries; when it gives up, the sent cursor is
    /// still advanced so the metric rows become purgeable (at-least-once until then).</item>
    /// <item>Messages that can never be delivered (unknown type, unreadable payload) are dropped
    /// immediately rather than blocking the queue.</item>
    /// </list>
    /// </remarks>
    private async Task FlushOutboxAsync(PortalOptions opts, CancellationToken ct)
    {
        var messages = await _outboxStore.GetPendingMessagesAsync(opts.Outbox.MaxBatchSize);
        if (messages.Count == 0) return;

        _logger.LogDebug("Flushing {Count} outbox messages", messages.Count);

        var eventMaxAge = TimeSpan.FromHours(Math.Max(1, opts.Outbox.EventMaxAgeHours));
        int sentCount = 0, expiredEvents = 0, undeliverable = 0, droppedBatches = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var msg = messages[i];

            if (msg.MessageType == EventMessageType && msg.CreatedAt is { } createdAt && UtcNow - createdAt > eventMaxAge)
            {
                await _outboxStore.MarkDroppedAsync(msg.Id, $"Expired after {eventMaxAge.TotalHours:0}h undelivered");
                expiredEvents++;
                continue;
            }

            Func<CancellationToken, Task<bool>>? send;
            try
            {
                send = PrepareSend(msg);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogError(ex, "Outbox message {Id} ({Type}) has an unreadable payload", msg.Id, msg.MessageType);
                send = null;
            }

            if (send == null)
            {
                await _outboxStore.MarkDroppedAsync(msg.Id, "Undeliverable: unknown type or unreadable payload");
                await AdvanceSentCursorAsync(msg); // never deliverable: do not stall metric purging
                undeliverable++;
                continue;
            }

            bool sent;
            try
            {
                sent = await send(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error sending outbox message {Id}", msg.Id);
                sent = false;
            }

            if (sent)
            {
                await _outboxStore.MarkSentAsync(msg.Id);
                await AdvanceSentCursorAsync(msg);
                sentCount++;
                continue;
            }

            if (await RecordFailureAsync(msg, opts))
                droppedBatches++;

            var deferred = messages.Count - i - 1;
            if (deferred > 0)
            {
                _logger.LogWarning(
                    "Portal send failed; deferring {Deferred} remaining outbox message(s) to the next cycle",
                    deferred);
            }
            break;
        }

        if (expiredEvents > 0 || undeliverable > 0 || droppedBatches > 0)
        {
            _logger.LogError(
                "Outbox dropped {Expired} expired event(s), {Undeliverable} undeliverable message(s) and " +
                "{Batches} metrics batch(es) that exhausted their retries this cycle",
                expiredEvents, undeliverable, droppedBatches);
        }

        if (sentCount > 0)
            _logger.LogDebug("Delivered {Count} outbox message(s)", sentCount);
    }

    /// <summary>Schedules the retry for a failed send. Returns true if a metrics batch was given up.</summary>
    private async Task<bool> RecordFailureAsync(OutboxMessage msg, PortalOptions opts)
    {
        var nextAttempt = msg.Attempts + 1;

        if (msg.MessageType == EventMessageType)
        {
            // Incidents are the most valuable payload: keep retrying (bounded by age, not count).
            await _outboxStore.MarkFailedAsync(
                msg.Id, "Send failed", UtcNow + EventRetryDelay(nextAttempt), maxAttempts: int.MaxValue);
            return false;
        }

        bool terminal = nextAttempt >= opts.Outbox.MaxRetryAttempts;
        DateTime? nextRetry = terminal
            ? null
            : UtcNow.AddSeconds(opts.Outbox.RetryBackoffSeconds * nextAttempt);

        await _outboxStore.MarkFailedAsync(msg.Id, "Send failed", nextRetry, opts.Outbox.MaxRetryAttempts);

        if (!terminal)
            return false;

        // The batch exhausted all retries (portal unreachable for the whole window). Advance
        // the sent cursor so these rows become purgeable and the pipeline does not stall, but
        // log loudly: this is dropped data.
        _logger.LogError(
            "Dropping metrics batch {Id} after {Attempts} failed attempts; cpu<= {Cpu}, ram<= {Ram} will not reach the portal",
            msg.Id, nextAttempt, msg.CpuRowId, msg.RamRowId);
        await AdvanceSentCursorAsync(msg);
        return true;
    }

    /// <summary>30s, 60s, 120s, ... capped at 15 min.</summary>
    internal static TimeSpan EventRetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var seconds = EventRetryBaseDelay.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, EventRetryMaxDelay.TotalSeconds));
    }

    private async Task AdvanceSentCursorAsync(OutboxMessage msg)
    {
        if (msg.MessageType != MetricsMessageType)
            return;

        if (msg.CpuRowId is long cpu && cpu > await _syncState.GetLastForwardedRowIdAsync("cpu_sent"))
            await _syncState.SetLastForwardedRowIdAsync("cpu_sent", cpu);
        if (msg.RamRowId is long ram && ram > await _syncState.GetLastForwardedRowIdAsync("ram_sent"))
            await _syncState.SetLastForwardedRowIdAsync("ram_sent", ram);
    }

    /// <summary>
    /// Deserializes the payload up front. Returns null for messages that can never be delivered.
    /// </summary>
    private Func<CancellationToken, Task<bool>>? PrepareSend(OutboxMessage msg)
    {
        switch (msg.MessageType)
        {
            case MetricsMessageType:
                var batch = JsonSerializer.Deserialize<MetricsBatchDto>(msg.PayloadJson, JsonOpts);
                return batch == null ? null : ct => _portalClient.SendMetricsBatchAsync(batch, ct);
            case EventMessageType:
                var evt = JsonSerializer.Deserialize<AgentEventDto>(msg.PayloadJson, JsonOpts);
                return evt == null ? null : ct => _portalClient.SendEventAsync(evt, ct);
            default:
                return null;
        }
    }
}