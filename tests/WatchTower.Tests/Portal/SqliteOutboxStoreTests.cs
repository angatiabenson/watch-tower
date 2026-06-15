using FluentAssertions;
using WatchTower.Portal.Storage;

namespace WatchTower.Tests.Portal;

public class SqliteOutboxStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteOutboxStore _store;

    public SqliteOutboxStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_outbox_{Guid.NewGuid()}.db");
        _store = new SqliteOutboxStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + ext;
            if (File.Exists(path)) { try { File.Delete(path); } catch { /* best effort */ } }
        }
    }

    [Fact]
    public async Task AddMessage_PersistsRowIdCoverage()
    {
        await _store.AddMessageAsync("MetricsBatch", "{}", priority: 0, cpuRowId: 42, ramRowId: 7);

        var pending = await _store.GetPendingMessagesAsync(10);
        pending.Should().HaveCount(1);
        pending[0].CpuRowId.Should().Be(42);
        pending[0].RamRowId.Should().Be(7);
    }

    [Fact]
    public async Task MarkSent_RemovesMessageFromPending()
    {
        await _store.AddMessageAsync("AgentEvent", "{}");
        var pending = await _store.GetPendingMessagesAsync(10);

        await _store.MarkSentAsync(pending[0].Id);

        (await _store.GetPendingMessagesAsync(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task MarkFailed_BecomesTerminalAtMaxAttempts()
    {
        await _store.AddMessageAsync("AgentEvent", "{}");
        var id = (await _store.GetPendingMessagesAsync(10))[0].Id;

        // First failure with maxAttempts=2 keeps it pending (no next-retry gate -> visible).
        await _store.MarkFailedAsync(id, "err", nextRetryAt: null, maxAttempts: 2);
        (await _store.GetPendingMessagesAsync(10)).Should().ContainSingle();

        // Second failure reaches maxAttempts -> terminal 'Failed', no longer pending.
        await _store.MarkFailedAsync(id, "err", nextRetryAt: null, maxAttempts: 2);
        (await _store.GetPendingMessagesAsync(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task SyncState_RoundTripsCursor()
    {
        (await _store.GetLastForwardedRowIdAsync("cpu_sent")).Should().Be(0);

        await _store.SetLastForwardedRowIdAsync("cpu_sent", 123);

        (await _store.GetLastForwardedRowIdAsync("cpu_sent")).Should().Be(123);
    }

    [Fact]
    public async Task PendingMessages_OrderedByPriorityThenId()
    {
        await _store.AddMessageAsync("MetricsBatch", "metrics", priority: 0);
        await _store.AddMessageAsync("AgentEvent", "event", priority: 1);

        var pending = await _store.GetPendingMessagesAsync(10);

        // Higher priority (events) first.
        pending[0].MessageType.Should().Be("AgentEvent");
        pending[1].MessageType.Should().Be("MetricsBatch");
    }
}
