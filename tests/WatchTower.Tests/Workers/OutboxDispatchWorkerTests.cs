using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class OutboxDispatchWorkerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-10T00:00:00Z"));
    private readonly Mock<IPortalClient> _portal = new();
    private readonly Mock<IOutboxStore> _outbox = new();
    private readonly Mock<IPortalSyncState> _syncState = new();
    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly Mock<IOptionsMonitor<PortalOptions>> _options = new();
    private List<OutboxMessage> _pending = new();

    public OutboxDispatchWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new PortalOptions
        {
            Enabled = true,
            Outbox = new OutboxOptions { MaxRetryAttempts = 5, RetryBackoffSeconds = 60, EventMaxAgeHours = 168 },
        });
        _metrics.Setup(m => m.GetCpuMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((0L, (IReadOnlyList<CpuMetric>)Array.Empty<CpuMetric>()));
        _metrics.Setup(m => m.GetRamMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((0L, (IReadOnlyList<RamMetric>)Array.Empty<RamMetric>()));
        _outbox.Setup(o => o.GetPendingMessagesAsync(It.IsAny<int>())).ReturnsAsync(() => _pending);
        _syncState.Setup(s => s.GetLastForwardedRowIdAsync(It.IsAny<string>())).ReturnsAsync(0L);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private OutboxMessage Event(long id, int attempts = 0, TimeSpan? age = null) => new(
        id, "AgentEvent",
        JsonSerializer.Serialize(new AgentEventDto($"evt-{id}", Now.ToString("O"), 4, "disk critical", null), JsonOpts),
        Priority: 1, Attempts: attempts, CreatedAt: Now - (age ?? TimeSpan.FromMinutes(1)));

    private OutboxMessage Batch(long id, int attempts = 0, long cpuRowId = 10, long ramRowId = 20) => new(
        id, "MetricsBatch",
        JsonSerializer.Serialize(new MetricsBatchDto("agent", $"b-{id}", Now.ToString("O"),
            new(), new(), new(), new(), new(), new(), new()), JsonOpts),
        Priority: 0, Attempts: attempts, CpuRowId: cpuRowId, RamRowId: ramRowId, CreatedAt: Now);

    private OutboxDispatchWorker CreateWorker() => new(
        _portal.Object, _outbox.Object, _syncState.Object, _metrics.Object, new MetricSnapshotTrigger(),
        _options.Object, _time, NullLogger<OutboxDispatchWorker>.Instance);

    private void PortalUp(bool up)
    {
        _portal.Setup(p => p.SendEventAsync(It.IsAny<AgentEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(up);
        _portal.Setup(p => p.SendMetricsBatchAsync(It.IsAny<MetricsBatchDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(up);
    }

    [Fact]
    public async Task FailedEvent_IsNeverTerminal_AndBacksOffExponentially()
    {
        PortalUp(false);
        _pending = new() { Event(1, attempts: 0) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        // Previously: retry at 60s*n and terminal 'Failed' after MaxRetryAttempts (~10 min down).
        _outbox.Verify(o => o.MarkFailedAsync(1, It.IsAny<string>(), Now + TimeSpan.FromSeconds(30), int.MaxValue), Times.Once);
    }

    [Fact]
    public async Task FailedEvent_PastMaxRetryAttempts_StillRetriesWithCappedDelay()
    {
        PortalUp(false);
        _pending = new() { Event(1, attempts: 50) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _outbox.Verify(o => o.MarkFailedAsync(1, It.IsAny<string>(), Now + TimeSpan.FromMinutes(15), int.MaxValue), Times.Once);
        _outbox.Verify(o => o.MarkDroppedAsync(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExpiredEvent_IsDroppedWithoutSending()
    {
        PortalUp(true);
        _pending = new() { Event(1, age: TimeSpan.FromHours(169)), Event(2) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _outbox.Verify(o => o.MarkDroppedAsync(1, It.Is<string>(s => s.Contains("Expired"))), Times.Once);
        _portal.Verify(p => p.SendEventAsync(It.Is<AgentEventDto>(e => e.EventId == "evt-1"), It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.MarkSentAsync(2), Times.Once);
    }

    [Fact]
    public async Task FirstFailure_StopsTheRestOfTheBatch()
    {
        PortalUp(false);
        _pending = new() { Event(1), Event(2), Batch(3) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _portal.Verify(p => p.SendEventAsync(It.IsAny<AgentEventDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _portal.Verify(p => p.SendMetricsBatchAsync(It.IsAny<MetricsBatchDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.MarkFailedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task SuccessfulSends_DeliverEverything_AndAdvanceSentCursor()
    {
        PortalUp(true);
        _pending = new() { Event(1), Batch(2, cpuRowId: 42, ramRowId: 7) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _outbox.Verify(o => o.MarkSentAsync(1), Times.Once);
        _outbox.Verify(o => o.MarkSentAsync(2), Times.Once);
        _syncState.Verify(s => s.SetLastForwardedRowIdAsync("cpu_sent", 42), Times.Once);
        _syncState.Verify(s => s.SetLastForwardedRowIdAsync("ram_sent", 7), Times.Once);
    }

    [Fact]
    public async Task MetricsBatch_KeepsBoundedRetries_AndAdvancesCursorWhenGivingUp()
    {
        PortalUp(false);
        _pending = new() { Batch(1, attempts: 4, cpuRowId: 99, ramRowId: 98) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _outbox.Verify(o => o.MarkFailedAsync(1, It.IsAny<string>(), null, 5), Times.Once);
        _syncState.Verify(s => s.SetLastForwardedRowIdAsync("cpu_sent", 99), Times.Once);
        _syncState.Verify(s => s.SetLastForwardedRowIdAsync("ram_sent", 98), Times.Once);
    }

    [Fact]
    public async Task MetricsBatch_NonTerminalFailure_DoesNotAdvanceCursor()
    {
        PortalUp(false);
        _pending = new() { Batch(1, attempts: 0) };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _outbox.Verify(o => o.MarkFailedAsync(1, It.IsAny<string>(), Now.AddSeconds(60), 5), Times.Once);
        _syncState.Verify(s => s.SetLastForwardedRowIdAsync(It.Is<string>(k => k.EndsWith("_sent")), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task UnreadablePayload_IsDropped_AndDoesNotBlockOthers()
    {
        PortalUp(true);
        _pending = new()
        {
            new OutboxMessage(1, "AgentEvent", "{not json", 1, 0, CreatedAt: Now),
            new OutboxMessage(2, "Mystery", "{}", 0, 0, CreatedAt: Now),
            Event(3),
        };

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _outbox.Verify(o => o.MarkDroppedAsync(1, It.IsAny<string>()), Times.Once);
        _outbox.Verify(o => o.MarkDroppedAsync(2, It.IsAny<string>()), Times.Once);
        _outbox.Verify(o => o.MarkSentAsync(3), Times.Once);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(5, 480)]
    [InlineData(6, 900)]
    [InlineData(1000, 900)]
    public void EventRetryDelay_DoublesFrom30sAndCapsAt15Minutes(int attempt, int expectedSeconds)
    {
        OutboxDispatchWorker.EventRetryDelay(attempt).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}