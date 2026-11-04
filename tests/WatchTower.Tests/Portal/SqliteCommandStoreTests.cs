using AwesomeAssertions;

using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;

namespace WatchTower.Tests.Portal;

public class SqliteCommandStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteCommandStore _store;

    public SqliteCommandStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_cmd_{Guid.NewGuid()}.db");
        _store = new SqliteCommandStore(_dbPath);
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

    private static AgentCommandDto Command(string id, string? scheduledAt = null) =>
        new(id, (int)WatchTower.Portal.Actions.ActionType.ServiceRestart,
            new Dictionary<string, string> { ["ServiceName"] = "Svc" },
            scheduledAt, DateTime.UtcNow.ToString("O"));

    [Fact]
    public async Task TryBeginExecution_OnlyFirstCallerWinsTheClaim()
    {
        await _store.SaveCommandAsync(Command("cmd-1"));

        var first = await _store.TryBeginExecutionAsync("cmd-1");
        var second = await _store.TryBeginExecutionAsync("cmd-1");

        // The atomic claim ensures the poll path and scheduled path cannot both run it.
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordRejected_StoresAFailedCommandWithAnUnsentResult_ThatCannotBeExecuted()
    {
        (await _store.TryRecordRejectedAsync(Command("old"), "Rejected: command is stale")).Should().BeTrue();

        var unsent = (await _store.GetUnsentResultsAsync()).Should().ContainSingle().Subject;
        unsent.CommandId.Should().Be("old");
        unsent.Status.Should().Be("Failed");
        var result = System.Text.Json.JsonSerializer.Deserialize<CommandResultDto>(unsent.ResultJson!)!;
        result.Success.Should().BeFalse();
        result.Message.Should().Be("Rejected: command is stale");
        (await _store.TryBeginExecutionAsync("old")).Should().BeFalse();
        (await _store.GetDueScheduledCommandsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TryRecordRejected_KnownCommandId_ChangesNothing()
    {
        await _store.SaveCommandAsync(Command("done"));
        (await _store.TryBeginExecutionAsync("done")).Should().BeTrue();
        await _store.MarkCompletedAsync("done", new CommandResultDto("done", true, "ok", DateTime.UtcNow.ToString("O")));
        await _store.MarkResultSentAsync("done");

        (await _store.TryRecordRejectedAsync(Command("done"), "Rejected: command is stale")).Should().BeFalse();
        (await _store.TryRecordRejectedAsync(Command("twice"), "Rejected: a")).Should().BeTrue();
        (await _store.TryRecordRejectedAsync(Command("twice"), "Rejected: b")).Should().BeFalse();

        (await _store.GetUnsentResultsAsync()).Should().ContainSingle()
            .Which.ResultJson.Should().Contain("Rejected: a");
    }

    [Fact]
    public async Task TryBeginExecution_UnknownCommand_ReturnsFalse()
    {
        (await _store.TryBeginExecutionAsync("does-not-exist")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveCommand_IsIdempotentOnCommandId()
    {
        await _store.SaveCommandAsync(Command("dup"));
        await _store.SaveCommandAsync(Command("dup")); // duplicate delivery

        // INSERT OR IGNORE: the second save does not overwrite/duplicate; claim still works once.
        (await _store.TryBeginExecutionAsync("dup")).Should().BeTrue();
        (await _store.TryBeginExecutionAsync("dup")).Should().BeFalse();
    }

    [Fact]
    public async Task GetDueScheduledCommands_ReturnsOnlyPastDue()
    {
        await _store.SaveCommandAsync(Command("past", DateTime.UtcNow.AddMinutes(-5).ToString("O")));
        await _store.SaveCommandAsync(Command("future", DateTime.UtcNow.AddHours(1).ToString("O")));

        var due = await _store.GetDueScheduledCommandsAsync();

        due.Select(d => d.CommandId).Should().Contain("past");
        due.Select(d => d.CommandId).Should().NotContain("future");
    }

    [Fact]
    public async Task FailInterruptedCommands_FailsOnlyExecutingRows_AndQueuesTheirResults()
    {
        await _store.SaveCommandAsync(Command("stuck"));
        await _store.SaveCommandAsync(Command("waiting"));
        await _store.TryBeginExecutionAsync("stuck");

        var results = await _store.FailInterruptedCommandsAsync("agent stopped");

        results.Should().ContainSingle(r => r.CommandId == "stuck" && !r.Success && r.Message == "agent stopped");
        var unsent = await _store.GetUnsentResultsAsync();
        unsent.Should().ContainSingle(r => r.CommandId == "stuck" && r.Status == "Failed");
        (await _store.TryBeginExecutionAsync("waiting")).Should().BeTrue();
        (await _store.FailInterruptedCommandsAsync("again")).Select(r => r.CommandId).Should().Equal("waiting");
    }

    [Fact]
    public async Task PurgeFinishedCommands_DeletesOnlyOldFinishedRows()
    {
        await _store.SaveCommandAsync(Command("done"));
        await _store.SaveCommandAsync(Command("pending"));
        await _store.MarkFailedAsync("done", "x");
        await _store.MarkResultSentAsync("done"); // only delivered results may be purged

        // Cutoff in the past: nothing is old enough yet.
        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddDays(-30))).Should().Be(0);

        // Cutoff in the future: the finished row qualifies, the pending one never does.
        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1))).Should().Be(1);
        (await _store.TryBeginExecutionAsync("pending")).Should().BeTrue();
    }

    private async Task Finish(string id)
    {
        await _store.SaveCommandAsync(Command(id));
        await _store.TryBeginExecutionAsync(id);
        await _store.MarkCompletedAsync(id, new CommandResultDto(id, true, "ok", DateTime.UtcNow.ToString("O")));
        await _store.MarkResultSentAsync(id);
    }

    [Fact]
    public async Task SaveCommand_ReturnsWhetherTheCommandIsWaitingForExecution()
    {
        (await _store.SaveCommandAsync(Command("new"))).Should().BeTrue();
        (await _store.SaveCommandAsync(Command("new"))).Should().BeTrue("a re-delivery of a command not claimed yet");
        (await _store.TryBeginExecutionAsync("new")).Should().BeTrue();
        (await _store.SaveCommandAsync(Command("new"))).Should().BeFalse("executing");

        await _store.TryRecordRejectedAsync(Command("rejected"), "Rejected: x");
        (await _store.SaveCommandAsync(Command("rejected"))).Should().BeFalse();
        (await _store.TryBeginExecutionAsync("rejected")).Should().BeFalse();
    }

    [Fact]
    public async Task KnownCommandId_IsNeverStoredAgain_AfterItsCommandRowWasPurged()
    {
        await Finish("done");
        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1))).Should().Be(1);

        (await _store.SaveCommandAsync(Command("done"))).Should().BeFalse();
        (await _store.TryBeginExecutionAsync("done")).Should().BeFalse();
        (await _store.TryRecordRejectedAsync(Command("done"), "Rejected: command is stale")).Should().BeFalse();
        (await _store.GetUnsentResultsAsync()).Should().BeEmpty();
        _store.GetCommandIdOutcome("done").Should().Be("Completed");
    }

    [Fact]
    public async Task CommandIdOutcome_FollowsTheCommand()
    {
        await _store.SaveCommandAsync(Command("a"));
        _store.GetCommandIdOutcome("a").Should().Be("Received");
        await _store.TryBeginExecutionAsync("a");
        _store.GetCommandIdOutcome("a").Should().Be("Executing");
        await _store.FailInterruptedCommandsAsync("stopped");
        _store.GetCommandIdOutcome("a").Should().Be("Failed");

        await _store.SaveCommandAsync(Command("unauthorized"));
        await _store.MarkFailedAsync("unauthorized", "Not authorized: x");
        _store.GetCommandIdOutcome("unauthorized").Should().Be("Failed");

        await _store.TryRecordRejectedAsync(Command("r"), "Rejected: x");
        _store.GetCommandIdOutcome("r").Should().Be("Rejected");
    }

    [Fact]
    public async Task PruneCommandIds_ForgetsIdsOutsideTheWindow_ButNotThoseWithACommandRow()
    {
        await Finish("purged");
        await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1));
        await Finish("kept-row");

        (await _store.PruneCommandIdsAsync(DateTime.UtcNow.AddDays(-1), 0)).Should().Be(0, "both are inside the window");
        (await _store.PruneCommandIdsAsync(DateTime.UtcNow.AddMinutes(1), 0)).Should().Be(1);

        _store.GetCommandIdOutcome("purged").Should().BeNull();
        _store.GetCommandIdOutcome("kept-row").Should().Be("Completed", "its command row still exists");
        (await _store.SaveCommandAsync(Command("kept-row"))).Should().BeFalse();
    }

    [Fact]
    public async Task PruneCommandIds_KeepsAtMostMaxCount_ForgettingTheOldestFirst()
    {
        for (var i = 0; i < 10; i++)
            await Finish($"c{i}");
        await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1));
        await _store.SaveCommandAsync(Command("live")); // has a command row: never pruned

        (await _store.PruneCommandIdsAsync(null, 4)).Should().Be(7);

        _store.CountCommandIds().Should().Be(4);
        foreach (var id in new[] { "c7", "c8", "c9", "live" })
            _store.GetCommandIdOutcome(id).Should().NotBeNull(id);
        (await _store.PruneCommandIdsAsync(null, 4)).Should().Be(0);
        (await _store.PruneCommandIdsAsync(null, 0)).Should().Be(0, "0 is no limit");
    }

    [Fact]
    public async Task ExistingDatabase_IsBackfilledWithTheIdsOfItsCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower_cmd_legacy_{Guid.NewGuid()}.db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // The agent_commands table as created before command ids were remembered.
                cmd.CommandText =
                    @"CREATE TABLE agent_commands (
                        command_id TEXT PRIMARY KEY, action_type INTEGER NOT NULL, params_json TEXT NOT NULL,
                        status TEXT NOT NULL DEFAULT 'Received', received_at TEXT NOT NULL, scheduled_at TEXT,
                        issued_at TEXT, acked_at TEXT, executed_at TEXT, result_json TEXT);
                      INSERT INTO agent_commands (command_id, action_type, params_json, status, received_at, executed_at, result_json)
                      VALUES ('old-done', 302, '{}', 'Completed', '2026-10-01T00:00:00.0000000Z', '2026-10-01T00:00:01.0000000Z', NULL),
                             ('old-waiting', 302, '{}', 'Received', '2026-10-02T00:00:00.0000000Z', NULL, NULL),
                             ('old-rejected', 302, '{}', 'Failed', '2026-10-03T00:00:00.0000000Z', '2026-10-03T00:00:00.0000000Z',
                              '{""CommandId"":""old-rejected"",""Success"":false,""Message"":""Rejected: command is stale"",""ExecutedAt"":""x""}'),
                             ('old-broken', 302, '{}', 'Failed', '2026-10-04T00:00:00.0000000Z', '2026-10-04T00:00:00.0000000Z', '{not json');";
                cmd.ExecuteNonQuery();
            }

            var store = new SqliteCommandStore(path);

            store.GetCommandIdOutcome("old-done").Should().Be("Completed");
            store.GetCommandIdOutcome("old-waiting").Should().Be("Received");
            store.CountRejections().Should().Be(1, "a waiting rejection recorded before the flag existed is recognised");
            (await store.GetDueResultsAsync(DateTime.UtcNow, 10)).Select(r => r.CommandId)
                .Should().Equal("old-rejected", "old-broken");
            (await store.PurgeFinishedCommandsAsync(DateTime.UtcNow)).Should().Be(1);
            (await store.SaveCommandAsync(Command("old-done"))).Should().BeFalse("its id survived the purge");
            (await store.SaveCommandAsync(Command("old-waiting"))).Should().BeTrue("still waiting to run");

            new SqliteCommandStore(path).CountCommandIds().Should().Be(4, "the migration is idempotent");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + ext); } catch (IOException) { /* best effort */ }
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(1_000, 30)]
    public void ResultRetryDelay_DoublesFromOneMinuteUpToThirty(int failedAttempts, int minutes)
    {
        SqliteCommandStore.ResultRetryDelay(failedAttempts).Should().Be(TimeSpan.FromMinutes(minutes));
    }

    private async Task Produce(string id)
    {
        await _store.SaveCommandAsync(Command(id));
        await _store.TryBeginExecutionAsync(id);
        await _store.MarkCompletedAsync(id, new CommandResultDto(id, true, "ok", DateTime.UtcNow.ToString("O")));
    }

    [Fact]
    public async Task FailedResultAttempt_IsNotDueUntilItsBackoffHasPassed()
    {
        await Produce("c1");
        var now = DateTime.UtcNow;

        (await _store.RecordResultAttemptFailedAsync("c1", now)).Should().Be(now + TimeSpan.FromMinutes(1));
        (await _store.GetDueResultsAsync(now.AddSeconds(59), 50)).Should().BeEmpty();
        (await _store.GetDueResultsAsync(now.AddMinutes(1), 50)).Should().ContainSingle(r => r.CommandId == "c1");

        (await _store.RecordResultAttemptFailedAsync("c1", now.AddMinutes(1))).Should().Be(now + TimeSpan.FromMinutes(3));
        (await _store.GetUnsentResultsAsync()).Should().ContainSingle("it is still waiting, only not due");

        await _store.MarkResultSentAsync("c1");
        (await _store.RecordResultAttemptFailedAsync("c1", now)).Should().BeNull("nothing is waiting any more");
    }

    [Fact]
    public async Task DueResults_AreTheLongestDueFirst_NotTheOldest()
    {
        await Produce("refused");
        await Produce("newer");
        var now = DateTime.UtcNow.AddSeconds(1);
        await _store.RecordResultAttemptFailedAsync("refused", now.AddMinutes(-10));

        (await _store.GetDueResultsAsync(now, 1)).Select(r => r.CommandId).Should().Equal("refused");
        await _store.RecordResultAttemptFailedAsync("refused", now);
        (await _store.GetDueResultsAsync(now, 1)).Select(r => r.CommandId).Should().Equal("newer");
    }

    [Fact]
    public async Task AbandonedResult_IsNoLongerRetried_AndItsRowIsPurgedWithTheHistory()
    {
        await Produce("old");
        var later = DateTime.UtcNow.AddMinutes(1);
        await Produce("recent");

        (await _store.AbandonUndeliveredResultsAsync(DateTime.UtcNow.AddDays(-7), later)).Should().BeEmpty();
        var abandoned = await _store.AbandonUndeliveredResultsAsync(later, later);
        abandoned.Select(a => a.CommandId).Should().BeEquivalentTo(["old", "recent"]);
        (await _store.AbandonUndeliveredResultsAsync(later, later)).Should().BeEmpty("only once");

        (await _store.GetDueResultsAsync(later.AddDays(1), 50)).Should().BeEmpty();
        (await _store.GetUnsentResultsAsync()).Should().BeEmpty();
        (await _store.PurgeFinishedCommandsAsync(later)).Should().Be(2);
        (await _store.SaveCommandAsync(Command("old"))).Should().BeFalse("its id is still remembered");
    }

    [Fact]
    public async Task TrimRejections_KeepsTheNewest_AndReportsTheUndeliveredOnesDropped()
    {
        for (var i = 0; i < 5; i++)
            await _store.TryRecordRejectedAsync(Command($"r{i}"), "Rejected: x");
        await _store.MarkResultSentAsync("r0");
        await Produce("executed"); // not a rejection: never trimmed

        var trimmed = await _store.TrimRejectionsAsync(2);

        trimmed.Deleted.Should().Be(3);
        trimmed.Undelivered.Select(u => u.CommandId).Should().BeEquivalentTo(["r1", "r2"]);
        _store.CountRejections().Should().Be(2);
        (await _store.GetUnsentResultsAsync()).Select(r => r.CommandId).Should().BeEquivalentTo(["r3", "r4", "executed"]);
        (await _store.TrimRejectionsAsync(2)).Deleted.Should().Be(0);
        (await _store.TryRecordRejectedAsync(Command("r0"), "Rejected: y")).Should().BeFalse("its id is still remembered");
    }

    [Fact]
    public async Task GetDueScheduledCommands_ExcludesAlreadyClaimed()
    {
        await _store.SaveCommandAsync(Command("past", DateTime.UtcNow.AddMinutes(-5).ToString("O")));
        await _store.TryBeginExecutionAsync("past"); // moves out of 'Received'

        var due = await _store.GetDueScheduledCommandsAsync();
        due.Should().BeEmpty();
    }
}