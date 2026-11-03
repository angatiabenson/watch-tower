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

        // Cutoff in the past: nothing is old enough yet.
        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddDays(-30))).Should().Be(0);

        // Cutoff in the future: the finished row qualifies, the pending one never does.
        (await _store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1))).Should().Be(1);
        (await _store.TryBeginExecutionAsync("pending")).Should().BeTrue();
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