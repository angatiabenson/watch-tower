using System.Text.Json;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
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
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger<OutboxDispatchWorker> _logger;

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
        IOptionsMonitor<PortalOptions> options,
        ILogger<OutboxDispatchWorker> logger)
    {
        _portalClient = portalClient;
        _outboxStore = outboxStore;
        _syncState = syncState;
        _metricsStore = metricsStore;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Dispatch Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;

                if (opts.Enabled)
                {
                    await CollectAndEnqueueMetricsAsync(opts, stoppingToken);
                    await FlushOutboxAsync(opts, stoppingToken);

                    _flushCycleCount++;
                    if (_flushCycleCount % 100 == 0)
                        await _outboxStore.PurgeDeliveredAsync(
                            TimeSpan.FromHours(opts.Outbox.PurgeDeliveredAfterHours));
                }

                var flushInterval = Math.Max(1, _options.CurrentValue.Outbox.FlushIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(flushInterval), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Outbox Dispatch Worker");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Outbox Dispatch Worker stopped");
    }

    private async Task CollectAndEnqueueMetricsAsync(PortalOptions opts, CancellationToken ct)
    {
        try
        {
            var maxBatch = opts.Outbox.MaxBatchSize;
            var agentId = opts.AgentId;
            var batchedAt = DateTime.UtcNow.ToString("O");
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

            bool hasData = cpuMetrics.Count > 0 || ramMetrics.Count > 0 ||
                           diskMetrics.Count > 0 || networkMetrics.Count > 0 ||
                           serviceStatuses.Count > 0 || appPoolStatuses.Count > 0;

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
                appPoolStatuses.Select(m => new ApplicationPoolStatusDto(m.Timestamp.ToString("O"), m.ApplicationPoolName, m.Status, m.IsRunning)).ToList()
            );

            await _outboxStore.AddMessageAsync(
                "MetricsBatch",
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

    private async Task FlushOutboxAsync(PortalOptions opts, CancellationToken ct)
    {
        var messages = await _outboxStore.GetPendingMessagesAsync(opts.Outbox.MaxBatchSize);
        if (messages.Count == 0) return;

        _logger.LogDebug("Flushing {Count} outbox messages", messages.Count);

        foreach (var msg in messages)
        {
            if (ct.IsCancellationRequested) break;

            bool sent;
            try
            {
                sent = msg.MessageType switch
                {
                    "MetricsBatch" => await SendMetricsBatchAsync(msg, ct),
                    "AgentEvent" => await SendEventAsync(msg, ct),
                    _ => false,
                };
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
            }
            else
            {
                var nextAttempt = msg.Attempts + 1;
                bool terminal = nextAttempt >= opts.Outbox.MaxRetryAttempts;
                DateTime? nextRetry = terminal
                    ? null
                    : DateTime.UtcNow.AddSeconds(opts.Outbox.RetryBackoffSeconds * nextAttempt);

                await _outboxStore.MarkFailedAsync(msg.Id, "Send failed", nextRetry, opts.Outbox.MaxRetryAttempts);

                if (terminal && msg.MessageType == "MetricsBatch")
                {
                    // The batch exhausted all retries (portal unreachable for the whole
                    // window). Advance the sent cursor so these rows become purgeable and
                    // the pipeline does not stall — but log loudly: this is dropped data.
                    _logger.LogError(
                        "Dropping metrics batch {Id} after {Attempts} failed attempts; cpu<= {Cpu}, ram<= {Ram} will not reach the portal",
                        msg.Id, nextAttempt, msg.CpuRowId, msg.RamRowId);
                    await AdvanceSentCursorAsync(msg);
                }
            }
        }
    }

    private async Task AdvanceSentCursorAsync(OutboxMessage msg)
    {
        if (msg.MessageType != "MetricsBatch")
            return;

        if (msg.CpuRowId is long cpu && cpu > await _syncState.GetLastForwardedRowIdAsync("cpu_sent"))
            await _syncState.SetLastForwardedRowIdAsync("cpu_sent", cpu);
        if (msg.RamRowId is long ram && ram > await _syncState.GetLastForwardedRowIdAsync("ram_sent"))
            await _syncState.SetLastForwardedRowIdAsync("ram_sent", ram);
    }

    private async Task<bool> SendMetricsBatchAsync(OutboxMessage msg, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<MetricsBatchDto>(msg.PayloadJson, JsonOpts);
        if (dto == null) return false;
        return await _portalClient.SendMetricsBatchAsync(dto, ct);
    }

    private async Task<bool> SendEventAsync(OutboxMessage msg, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<AgentEventDto>(msg.PayloadJson, JsonOpts);
        if (dto == null) return false;
        return await _portalClient.SendEventAsync(dto, ct);
    }

}