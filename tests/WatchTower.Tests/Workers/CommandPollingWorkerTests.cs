using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class CommandPollingWorkerTests
{
    private readonly Mock<IPortalClient> _portal = new();
    private readonly Mock<ICommandStore> _store = new();
    private readonly Mock<IActionHandler> _handler = new();
    private readonly Mock<IOptionsMonitor<PortalOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ServiceMonitorOptions>> _serviceOptions = new();
    private readonly Mock<IOptionsMonitor<ApplicationPoolMonitorOptions>> _poolOptions = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    public CommandPollingWorkerTests()
    {
        _store.Setup(s => s.FailInterruptedCommandsAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<CommandResultDto>());
        _store.Setup(s => s.PurgeFinishedCommandsAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
        _options.Setup(o => o.CurrentValue).Returns(new PortalOptions
        {
            Enabled = true,
            CommandPoll = new CommandPollOptions { PollIntervalSeconds = 30, MaxCommandAgeSeconds = 3600 },
        });
        _serviceOptions.Setup(o => o.CurrentValue).Returns(new ServiceMonitorOptions());
        _poolOptions.Setup(o => o.CurrentValue).Returns(new ApplicationPoolMonitorOptions());

        _handler.Setup(h => h.ActionType).Returns(ActionType.ForceConfigSync);
        _handler.Setup(h => h.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(true, "ok"));

        _store.Setup(s => s.SaveCommandAsync(It.IsAny<AgentCommandDto>())).ReturnsAsync(true);
        _store.Setup(s => s.TryBeginExecutionAsync(It.IsAny<string>())).ReturnsAsync(true);
        _store.Setup(s => s.TryRecordRejectedAsync(It.IsAny<AgentCommandDto>(), It.IsAny<string>())).ReturnsAsync(true);
        _store.Setup(s => s.GetDueScheduledCommandsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        _store.Setup(s => s.GetUnsentResultsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        _store.Setup(s => s.GetDueResultsAsync(It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        _store.Setup(s => s.AbandonUndeliveredResultsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(Array.Empty<UndeliveredResult>());
        _store.Setup(s => s.TrimRejectionsAsync(It.IsAny<int>())).ReturnsAsync(new TrimmedRejections(0, []));
        _portal.Setup(p => p.AcknowledgeCommandAsync(It.IsAny<AcknowledgeCommandDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private AgentCommandDto Command(string id, ActionType type = ActionType.ForceConfigSync,
        Dictionary<string, string>? parameters = null) =>
        new(id, (int)type, parameters ?? new Dictionary<string, string>(), null, _time.GetUtcNow().UtcDateTime.ToString("O"));

    private void Poll(params AgentCommandDto[] commands) =>
        _portal.Setup(p => p.PollCommandsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(commands);

    private void ResultAccepted(bool accepted) =>
        _portal.Setup(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted);

    private CommandPollingWorker CreateWorker(ILogger<CommandPollingWorker>? logger = null) => new(
        _portal.Object,
        _store.Object,
        new ActionHandlerRegistry(new[] { _handler.Object }, NullLogger<ActionHandlerRegistry>.Instance),
        _options.Object,
        _serviceOptions.Object,
        _poolOptions.Object,
        _time,
        logger ?? NullLogger<CommandPollingWorker>.Instance);

    [Fact]
    public async Task ExecutedCommand_ResultRejectedByPortal_IsNotMarkedSent()
    {
        Poll(Command("c1"));
        ResultAccepted(false);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        // ReportCommandResultAsync returns false instead of throwing; the result must stay
        // pending so RetryUnsentResultsAsync re-sends it.
        _store.Verify(s => s.MarkCompletedAsync("c1", It.IsAny<CommandResultDto>()), Times.Once);
        _store.Verify(s => s.MarkResultSentAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecutedCommand_ResultAccepted_IsMarkedSent()
    {
        Poll(Command("c1"));
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkResultSentAsync("c1"), Times.Once);
    }

    [Fact]
    public async Task RejectedCommand_ResultRejectedByPortal_IsNotMarkedSent()
    {
        // Unauthorized: targets a service that is not watched -> FailCommandAsync path.
        Poll(Command("c1", ActionType.ServiceRestart, new() { ["ServiceName"] = "NotWatched" }));
        ResultAccepted(false);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkFailedAsync("c1", It.Is<string>(m => m.Contains("Not authorized"))), Times.Once);
        _store.Verify(s => s.MarkResultSentAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnsentResult_IsRetriedAndMarkedSentOnceAccepted()
    {
        Poll();
        var json = System.Text.Json.JsonSerializer.Serialize(new CommandResultDto("c9", true, "done", DateTime.UtcNow.ToString("O")));
        _store.Setup(s => s.GetDueResultsAsync(Now, CommandPollingWorker.MaxResultsPerCycle)).ReturnsAsync(new[]
        {
            new AgentCommandRecord("c9", 302, "{}", "Completed", DateTime.UtcNow.ToString("O"), null, null, json),
        });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkResultSentAsync("c9"), Times.Once);
    }

    [Fact]
    public async Task ThrowingCommand_DoesNotAbortTheRestOfTheBatch()
    {
        Poll(Command("bad"), Command("good"));
        ResultAccepted(true);
        _store.Setup(s => s.SaveCommandAsync(It.Is<AgentCommandDto>(c => c.CommandId == "bad")))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkCompletedAsync("good", It.IsAny<CommandResultDto>()), Times.Once);
        _store.Verify(s => s.MarkCompletedAsync("bad", It.IsAny<CommandResultDto>()), Times.Never);
    }

    [Fact]
    public async Task NullParameters_AreTreatedAsEmpty()
    {
        Poll(Command("c1") with { Parameters = null! });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _handler.Verify(h => h.ExecuteAsync(
            It.Is<IReadOnlyDictionary<string, string>>(p => p != null && p.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SaveCommandAsync(It.Is<AgentCommandDto>(c => c.Parameters != null)), Times.Once);
    }

    [Fact]
    public async Task NullParameters_OnTargetedCommand_IsRejectedNotCrashed()
    {
        Poll(Command("c1", ActionType.ServiceRestart) with { Parameters = null! });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkFailedAsync("c1", It.Is<string>(m => m.Contains("missing ServiceName"))), Times.Once);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private int MissingIssuedAtWarnings(RecordingLoggerProvider logs) =>
        logs.At(LogLevel.Warning).Count(e => e.Message == CommandPollingWorker.MissingIssuedAtWarning);

    [Fact]
    public async Task MissingIssuedAt_IsExecuted_AndWarnedOncePerPortalSession()
    {
        var logs = new RecordingLoggerProvider();
        var worker = CreateWorker(logs.CreateLogger<CommandPollingWorker>());
        ResultAccepted(true);
        Poll(Command("c1") with { IssuedAt = "" }, Command("c2") with { IssuedAt = null! });

        await worker.RunCycleAsync(CancellationToken.None);
        Poll(Command("c3") with { IssuedAt = null! });
        await worker.RunCycleAsync(CancellationToken.None);

        foreach (var id in new[] { "c1", "c2", "c3" })
        {
            _store.Verify(s => s.SaveCommandAsync(It.Is<AgentCommandDto>(c => c.CommandId == id)), Times.Once);
            _portal.Verify(p => p.AcknowledgeCommandAsync(It.Is<AcknowledgeCommandDto>(a => a.CommandId == id), It.IsAny<CancellationToken>()), Times.Once);
            _store.Verify(s => s.MarkCompletedAsync(id, It.IsAny<CommandResultDto>()), Times.Once);
        }
        _store.Verify(s => s.TryRecordRejectedAsync(It.IsAny<AgentCommandDto>(), It.IsAny<string>()), Times.Never);
        MissingIssuedAtWarnings(logs).Should().Be(1);

        // A different portal is a new session: warn again (once).
        _options.Setup(o => o.CurrentValue).Returns(new PortalOptions
        {
            Enabled = true,
            BaseUrl = "https://other-portal.example",
            CommandPoll = new CommandPollOptions { PollIntervalSeconds = 30, MaxCommandAgeSeconds = 3600 },
        });
        Poll(Command("c4") with { IssuedAt = "" }, Command("c5") with { IssuedAt = "" });
        await worker.RunCycleAsync(CancellationToken.None);

        MissingIssuedAtWarnings(logs).Should().Be(2);
        _store.Verify(s => s.MarkCompletedAsync("c5", It.IsAny<CommandResultDto>()), Times.Once);
    }

    [Theory]
    [InlineData(-3600 - 300 + 1, "now - (max age + skew) + 1 s")]
    [InlineData(-3600, "exactly max age")]
    [InlineData(0, "now")]
    [InlineData(300, "exactly the allowed future skew")]
    public async Task IssuedAt_WithinLimits_IsExecuted(int offsetSeconds, string because)
    {
        Poll(Command("c1") with { IssuedAt = Now.AddSeconds(offsetSeconds).ToString("O") });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkCompletedAsync("c1", It.IsAny<CommandResultDto>()), Times.Once, because);
        _store.Verify(s => s.TryRecordRejectedAsync(It.IsAny<AgentCommandDto>(), It.IsAny<string>()), Times.Never, because);
    }

    [Theory]
    [InlineData(-3600 - 300 - 1, "command is stale: issuedAt 2025-12-31T22:54:59.0000000Z is more than 3900s old")]
    [InlineData(-86400, "command is stale: issuedAt 2025-12-31T00:00:00.0000000Z is more than 3900s old")]
    [InlineData(301, "issuedAt 2026-01-01T00:05:01.0000000Z is more than 300s in the future")]
    [InlineData(86400, "issuedAt 2026-01-02T00:00:00.0000000Z is more than 300s in the future")]
    public async Task IssuedAt_OutsideLimits_IsRejected_AndReportedAsAFailedResultWithTheReason(int offsetSeconds, string reason)
    {
        var command = Command("c1") with { IssuedAt = Now.AddSeconds(offsetSeconds).ToString("O") };
        Poll(command);
        ResultAccepted(true);
        CommandResultDto? reported = null;
        _portal.Setup(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()))
            .Callback<CommandResultDto, CancellationToken>((r, _) => reported = r)
            .ReturnsAsync(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.TryRecordRejectedAsync(command, It.Is<string>(m => m.StartsWith("Rejected: " + reason))), Times.Once);
        reported.Should().NotBeNull();
        reported!.CommandId.Should().Be("c1");
        reported.Success.Should().BeFalse();
        reported.Message.Should().StartWith("Rejected: " + reason);
        _store.Verify(s => s.MarkResultSentAsync("c1"), Times.Once);

        // Never stored for execution, acknowledged or executed.
        _store.Verify(s => s.SaveCommandAsync(It.IsAny<AgentCommandDto>()), Times.Never);
        _portal.Verify(p => p.AcknowledgeCommandAsync(It.IsAny<AcknowledgeCommandDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _handler.Verify(h => h.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnparseableIssuedAt_IsRejectedWithTheReason()
    {
        Poll(Command("c1") with { IssuedAt = "yesterday-ish" });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.TryRecordRejectedAsync(It.IsAny<AgentCommandDto>(), "Rejected: unparseable issuedAt 'yesterday-ish'"), Times.Once);
        _portal.Verify(p => p.ReportCommandResultAsync(
            It.Is<CommandResultDto>(r => !r.Success && r.Message == "Rejected: unparseable issuedAt 'yesterday-ish'"), It.IsAny<CancellationToken>()), Times.Once);
        _handler.Verify(h => h.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StaleReDeliveryOfAKnownCommand_ReportsNothing()
    {
        // The id is already stored (e.g. executed an hour ago and the portal re-sends it).
        _store.Setup(s => s.TryRecordRejectedAsync(It.IsAny<AgentCommandDto>(), It.IsAny<string>())).ReturnsAsync(false);
        Poll(Command("c1") with { IssuedAt = Now.AddHours(-5).ToString("O") });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _portal.Verify(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _handler.Verify(h => h.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RejectionResult_NotAcceptedByThePortal_StaysUnsent()
    {
        Poll(Command("c1") with { IssuedAt = Now.AddHours(-5).ToString("O") });
        ResultAccepted(false);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _portal.Verify(p => p.ReportCommandResultAsync(It.Is<CommandResultDto>(r => r.CommandId == "c1"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkResultSentAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RealStore_RejectedCommand_IsRetriedUntilAccepted_AndDuplicatesStayProtected()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_cmd_{Guid.NewGuid()}.db");
        try
        {
            var store = new SqliteCommandStore(dbPath);
            var worker = new CommandPollingWorker(
                _portal.Object, store,
                new ActionHandlerRegistry(new[] { _handler.Object }, NullLogger<ActionHandlerRegistry>.Instance),
                _options.Object, _serviceOptions.Object, _poolOptions.Object, _time, NullLogger<CommandPollingWorker>.Instance);

            // 1. Stale command, portal down: result stays stored.
            Poll(Command("stale") with { IssuedAt = Now.AddHours(-2).ToString("O") });
            ResultAccepted(false);
            await worker.RunCycleAsync(CancellationToken.None);
            (await store.GetUnsentResultsAsync()).Should().ContainSingle(r => r.CommandId == "stale" && r.Status == "Failed");

            // 2. Portal back, same stale command re-delivered: no second rejection; the stored one is
            //    retried once its 1-minute backoff has passed.
            ResultAccepted(true);
            _time.Advance(SqliteCommandStore.ResultRetryBaseDelay);
            await worker.RunCycleAsync(CancellationToken.None);
            (await store.GetUnsentResultsAsync()).Should().BeEmpty();
            _portal.Verify(p => p.ReportCommandResultAsync(
                It.Is<CommandResultDto>(r => r.CommandId == "stale" && !r.Success && r.Message.StartsWith("Rejected: command is stale")),
                It.IsAny<CancellationToken>()), Times.Exactly(2), "sent at rejection and by the retry after the backoff");

            // 3. A command without issuedAt runs once; re-delivered, it is not executed again.
            Poll(Command("legacy") with { IssuedAt = null! });
            await worker.RunCycleAsync(CancellationToken.None);
            await worker.RunCycleAsync(CancellationToken.None);
            _handler.Verify(h => h.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);

            // 4. The executed command re-delivered later with a stale issuedAt keeps its success.
            _portal.Invocations.Clear();
            Poll(Command("legacy") with { IssuedAt = Now.AddHours(-3).ToString("O") });
            await worker.RunCycleAsync(CancellationToken.None);
            _portal.Verify(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + ext); } catch (IOException) { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task MissingIssuedAt_IsAccepted_WhenAgeCheckDisabled()
    {
        _options.Setup(o => o.CurrentValue).Returns(new PortalOptions
        {
            Enabled = true,
            CommandPoll = new CommandPollOptions { PollIntervalSeconds = 30, MaxCommandAgeSeconds = 0 },
        });
        Poll(Command("c1") with { IssuedAt = "" });
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkCompletedAsync("c1", It.IsAny<CommandResultDto>()), Times.Once);
    }

    [Fact]
    public async Task InterruptedCommands_AreFailedAndReportedOnceAtStartup()
    {
        Poll();
        ResultAccepted(true);
        var interrupted = new CommandResultDto("stuck", false, CommandPollingWorker.InterruptedMessage, "2026-01-01T00:00:00Z");
        _store.Setup(s => s.FailInterruptedCommandsAsync(It.IsAny<string>())).ReturnsAsync(new[] { interrupted });
        var worker = CreateWorker();

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.FailInterruptedCommandsAsync(It.IsAny<string>()), Times.Once);
        _portal.Verify(p => p.ReportCommandResultAsync(interrupted, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkResultSentAsync("stuck"), Times.Once);
    }

    [Fact]
    public async Task FinishedCommands_ArePurgedHourlyAfterRetention()
    {
        Poll();
        var worker = CreateWorker();
        var expectedCutoff = _time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30);

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);
        _store.Verify(s => s.PurgeFinishedCommandsAsync(expectedCutoff), Times.Once);

        _time.Advance(CommandPollingWorker.PurgeInterval);
        await worker.RunCycleAsync(CancellationToken.None);
        _store.Verify(s => s.PurgeFinishedCommandsAsync(It.IsAny<DateTime>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ScheduledCommandWithUnreadableParameters_IsFailed_AndOthersStillRun()
    {
        Poll();
        ResultAccepted(true);
        var now = _time.GetUtcNow().UtcDateTime.ToString("O");
        _store.Setup(s => s.GetDueScheduledCommandsAsync()).ReturnsAsync(new[]
        {
            new AgentCommandRecord("broken", 302, "{not json", "Received", now, now, now, null),
            new AgentCommandRecord("fine", 302, "{}", "Received", now, now, now, null),
        });

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.MarkFailedAsync("broken", It.Is<string>(m => m.Contains("unreadable"))), Times.Once);
        _store.Verify(s => s.MarkCompletedAsync("fine", It.IsAny<CommandResultDto>()), Times.Once);
    }

    private void SetOptions(Action<PortalOptions> configure)
    {
        var opts = new PortalOptions
        {
            Enabled = true,
            CommandPoll = new CommandPollOptions { PollIntervalSeconds = 30, MaxCommandAgeSeconds = 3600 },
        };
        configure(opts);
        _options.Setup(o => o.CurrentValue).Returns(opts);
    }

    [Theory]
    [InlineData("", 3600)]
    [InlineData(null, 3600)]
    [InlineData("  ", 0)]
    public async Task RequireCommandIssuedAt_RejectsCommandsWithoutIt(string? issuedAt, int maxAge)
    {
        SetOptions(o =>
        {
            o.RequireCommandIssuedAt = true;
            o.CommandPoll.MaxCommandAgeSeconds = maxAge;
        });
        Poll(Command("c1") with { IssuedAt = issuedAt! }, Command("c2"));
        ResultAccepted(true);

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.TryRecordRejectedAsync(
            It.Is<AgentCommandDto>(c => c.CommandId == "c1"), "Rejected: issuedAt is required"), Times.Once);
        _portal.Verify(p => p.ReportCommandResultAsync(
            It.Is<CommandResultDto>(r => r.CommandId == "c1" && !r.Success && r.Message == "Rejected: issuedAt is required"),
            It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SaveCommandAsync(It.Is<AgentCommandDto>(c => c.CommandId == "c1")), Times.Never);
        _store.Verify(s => s.MarkCompletedAsync("c1", It.IsAny<CommandResultDto>()), Times.Never);
        // A command that carries issuedAt is unaffected.
        _store.Verify(s => s.MarkCompletedAsync("c2", It.IsAny<CommandResultDto>()), Times.Once);
    }

    [Fact]
    public async Task KnownCommand_IsAcknowledgedButNotExecuted()
    {
        _store.Setup(s => s.SaveCommandAsync(It.IsAny<AgentCommandDto>())).ReturnsAsync(false);
        Poll(Command("c1"));

        await CreateWorker().RunCycleAsync(CancellationToken.None);

        _portal.Verify(p => p.AcknowledgeCommandAsync(It.Is<AcknowledgeCommandDto>(a => a.CommandId == "c1"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.TryBeginExecutionAsync(It.IsAny<string>()), Times.Never);
        _store.Verify(s => s.MarkFailedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _handler.Verify(h => h.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CommandIds_ArePrunedHourly_WithTheConfiguredWindowAndCap()
    {
        SetOptions(o =>
        {
            o.CommandIdRetentionDays = 100;
            o.CommandIdMaxCount = 5000;
        });
        Poll();
        var worker = CreateWorker();

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);
        _store.Verify(s => s.PruneCommandIdsAsync(Now - TimeSpan.FromDays(100), 5000), Times.Once);

        // No age limit: only the cap applies. Pruned even when the result retention is off.
        SetOptions(o =>
        {
            o.CommandIdRetentionDays = 0;
            o.CommandPoll.RetentionDays = 0;
        });
        _time.Advance(CommandPollingWorker.PurgeInterval);
        await worker.RunCycleAsync(CancellationToken.None);
        _store.Verify(s => s.PruneCommandIdsAsync(null, 200_000), Times.Once);
    }

    private sealed class TempDb : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"watchtower_cmd_{Guid.NewGuid()}.db");

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(Path + ext); } catch (IOException) { /* best effort */ }
            }
        }
    }

    private CommandPollingWorker RealStoreWorker(ICommandStore store) => new(
        _portal.Object, store,
        new ActionHandlerRegistry(new[] { _handler.Object }, NullLogger<ActionHandlerRegistry>.Instance),
        _options.Object, _serviceOptions.Object, _poolOptions.Object, _time, NullLogger<CommandPollingWorker>.Instance);

    private int Executions() => _handler.Invocations.Count(i => i.Method.Name == nameof(IActionHandler.ExecuteAsync));

    [Fact]
    public async Task RealStore_ReplayWithoutIssuedAt_AfterResultRetention_IsNotExecutedAgain()
    {
        using var db = new TempDb();
        var store = new SqliteCommandStore(db.Path);
        ResultAccepted(true);

        Poll(Command("legacy") with { IssuedAt = null! });
        await RealStoreWorker(store).RunCycleAsync(CancellationToken.None);
        Executions().Should().Be(1);

        // The default 30-day result retention deletes the finished command row...
        (await store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1))).Should().Be(1);

        // ...and the same id replayed without issuedAt (so no age check applies) is still known.
        _portal.Invocations.Clear();
        await RealStoreWorker(new SqliteCommandStore(db.Path)).RunCycleAsync(CancellationToken.None);

        Executions().Should().Be(1, "a command id is never executed twice");
        _portal.Verify(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
        (await store.TryBeginExecutionAsync("legacy")).Should().BeFalse("no command row was stored again");
        store.GetCommandIdOutcome("legacy").Should().Be("Completed");
    }

    private int Sends(string commandId) => _portal.Invocations.Count(i =>
        i.Method.Name == nameof(IPortalClient.ReportCommandResultAsync) && i.Arguments[0] is CommandResultDto r && r.CommandId == commandId);

    private void ResultAccepted(Func<CommandResultDto, bool> accept) =>
        _portal.Setup(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommandResultDto r, CancellationToken _) => accept(r));

    [Fact]
    public async Task RealStore_RefusedResult_IsRetriedWithBackoff()
    {
        using var db = new TempDb();
        _time.SetUtcNow(DateTimeOffset.UtcNow); // the store stamps rows with the real clock
        var worker = RealStoreWorker(new SqliteCommandStore(db.Path));
        ResultAccepted(false);

        Poll(Command("c1"));
        await worker.RunCycleAsync(CancellationToken.None);
        Poll();
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await worker.RunCycleAsync(CancellationToken.None);
        }

        // Sent at execution, then 1, 2 and 4 minutes after each failed attempt: at 0, 1, 3 and 7 min.
        Sends("c1").Should().Be(4);
    }

    [Fact]
    public async Task RealStore_PermanentlyRefusedResults_DoNotBlockNewerOnes()
    {
        using var db = new TempDb();
        _time.SetUtcNow(DateTimeOffset.UtcNow);
        var worker = RealStoreWorker(new SqliteCommandStore(db.Path));
        var goodAccepted = false;
        ResultAccepted(r => !r.CommandId.StartsWith("bad-", StringComparison.Ordinal) && (r.CommandId != "good" || goodAccepted));

        // More refused results than one retry cycle sends (50), all older than the next one.
        Poll(Enumerable.Range(0, 60).Select(i => Command($"bad-{i}")).ToArray());
        await worker.RunCycleAsync(CancellationToken.None);

        // The portal is briefly down for the next result too.
        _time.Advance(TimeSpan.FromMinutes(1));
        Poll(Command("good"));
        await worker.RunCycleAsync(CancellationToken.None);
        Sends("good").Should().Be(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        goodAccepted = true;
        Poll();
        await worker.RunCycleAsync(CancellationToken.None);

        Sends("good").Should().Be(2, "due results go out in the order they are due, not by age");
        var store = new SqliteCommandStore(db.Path);
        (await store.GetUnsentResultsAsync()).Select(r => r.CommandId).Should().NotContain("good").And.HaveCount(60);
        Enumerable.Range(0, 60).Sum(i => Sends($"bad-{i}")).Should().Be(60 + 50 + 10,
            "each was sent once, then retried once after 1 minute (50 in one cycle, 10 in the next)");
    }

    [Fact]
    public async Task RealStore_ResultNeverAccepted_IsGivenUpOn_AndLogged()
    {
        using var db = new TempDb();
        _time.SetUtcNow(DateTimeOffset.UtcNow);
        SetOptions(o => o.CommandPoll.ResultRetryMaxHours = 2);
        var logs = new RecordingLoggerProvider();
        var store = new SqliteCommandStore(db.Path);
        var worker = new CommandPollingWorker(_portal.Object, store,
            new ActionHandlerRegistry(new[] { _handler.Object }, NullLogger<ActionHandlerRegistry>.Instance),
            _options.Object, _serviceOptions.Object, _poolOptions.Object, _time, logs.CreateLogger<CommandPollingWorker>());
        ResultAccepted(false);

        Poll(Command("c1"));
        await worker.RunCycleAsync(CancellationToken.None);
        Poll();
        for (var minutes = 0; minutes < 150; minutes += 5)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await worker.RunCycleAsync(CancellationToken.None);
        }

        (await store.GetUnsentResultsAsync()).Should().BeEmpty();
        var sends = Sends("c1");
        sends.Should().BeInRange(6, 9, "1, 2, 4, 8, 16, 30… minutes apart for 2 hours");
        logs.At(LogLevel.Warning).Should().ContainSingle(e => e.Message.StartsWith("Gave up delivering the result of command c1"));

        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            await worker.RunCycleAsync(CancellationToken.None);
        }
        Sends("c1").Should().Be(sends, "an abandoned result is not retried");
        (await store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1))).Should().Be(1, "abandoned rows are purged with the history");
    }

    [Fact]
    public async Task UndeliverableResults_AreSettledEveryCycle()
    {
        SetOptions(o => o.CommandPoll.ResultRetryMaxHours = 10);
        Poll();
        var worker = CreateWorker();

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);

        _store.Verify(s => s.AbandonUndeliveredResultsAsync(Now - TimeSpan.FromHours(10), Now), Times.Exactly(2));
        _store.Verify(s => s.TrimRejectionsAsync(CommandPollingWorker.DefaultMaxStoredRejections), Times.Exactly(2));
    }

    [Fact]
    public async Task RealStore_RejectionRows_AreBounded()
    {
        using var db = new TempDb();
        var store = new SqliteCommandStore(db.Path);
        var worker = new CommandPollingWorker(_portal.Object, store,
            new ActionHandlerRegistry(new[] { _handler.Object }, NullLogger<ActionHandlerRegistry>.Instance),
            _options.Object, _serviceOptions.Object, _poolOptions.Object, _time, NullLogger<CommandPollingWorker>.Instance)
        {
            MaxStoredRejections = 20,
        };
        ResultAccepted(false);

        // A portal that keeps sending stale commands and refusing their results.
        var stale = Now.AddHours(-5).ToString("O");
        for (var batch = 0; batch < 4; batch++)
        {
            Poll(Enumerable.Range(0, 15).Select(i => Command($"stale-{batch}-{i}") with { IssuedAt = stale }).ToArray());
            await worker.RunCycleAsync(CancellationToken.None);
            store.CountRejections().Should().BeLessThanOrEqualTo(20);
        }

        store.CountRejections().Should().Be(20);
        (await store.GetUnsentResultsAsync()).Select(r => r.CommandId).Should()
            .OnlyContain(id => id.StartsWith("stale-3-") || id.StartsWith("stale-2-"), "the newest are kept");
        store.CountCommandIds().Should().Be(60, "dropped rejections stay known");
        (await store.TryRecordRejectedAsync(Command("stale-0-0"), "Rejected: again")).Should().BeFalse();
    }

    /// <summary>Delegates to a real store but throws right after <see cref="CrashAfter"/> succeeded, like a crash.</summary>
    private sealed class CrashingStore(SqliteCommandStore inner) : ICommandStore
    {
        public string? CrashAfter { get; set; }

        private async Task<T> Then<T>(string method, Task<T> call)
        {
            var result = await call;
            if (CrashAfter == method)
                throw new InvalidOperationException($"simulated crash after {method}");
            return result;
        }

        public Task<bool> SaveCommandAsync(AgentCommandDto command) => Then(nameof(SaveCommandAsync), inner.SaveCommandAsync(command));
        public Task<bool> TryRecordRejectedAsync(AgentCommandDto command, string message) => inner.TryRecordRejectedAsync(command, message);
        public Task<int> PruneCommandIdsAsync(DateTime? seenBeforeUtc, int maxCount) => inner.PruneCommandIdsAsync(seenBeforeUtc, maxCount);
        public Task<IReadOnlyList<AgentCommandRecord>> GetPendingCommandsAsync() => inner.GetPendingCommandsAsync();
        public Task<bool> TryBeginExecutionAsync(string commandId) => Then(nameof(TryBeginExecutionAsync), inner.TryBeginExecutionAsync(commandId));
        public Task MarkCompletedAsync(string commandId, CommandResultDto result) => inner.MarkCompletedAsync(commandId, result);
        public Task MarkFailedAsync(string commandId, string error) => inner.MarkFailedAsync(commandId, error);
        public Task<IReadOnlyList<AgentCommandRecord>> GetUnsentResultsAsync() => inner.GetUnsentResultsAsync();
        public Task<IReadOnlyList<AgentCommandRecord>> GetDueResultsAsync(DateTime nowUtc, int limit) => inner.GetDueResultsAsync(nowUtc, limit);
        public Task<DateTime?> RecordResultAttemptFailedAsync(string commandId, DateTime nowUtc) => inner.RecordResultAttemptFailedAsync(commandId, nowUtc);
        public Task<IReadOnlyList<UndeliveredResult>> AbandonUndeliveredResultsAsync(DateTime producedBeforeUtc, DateTime nowUtc) =>
            inner.AbandonUndeliveredResultsAsync(producedBeforeUtc, nowUtc);
        public Task<TrimmedRejections> TrimRejectionsAsync(int keep) => inner.TrimRejectionsAsync(keep);
        public Task MarkResultSentAsync(string commandId) => inner.MarkResultSentAsync(commandId);
        public Task<IReadOnlyList<AgentCommandRecord>> GetDueScheduledCommandsAsync() => inner.GetDueScheduledCommandsAsync();
        public Task<IReadOnlyList<CommandResultDto>> FailInterruptedCommandsAsync(string message) => inner.FailInterruptedCommandsAsync(message);
        public Task<int> PurgeFinishedCommandsAsync(DateTime olderThanUtc) => inner.PurgeFinishedCommandsAsync(olderThanUtc);
    }

    [Theory]
    [InlineData(nameof(ICommandStore.TryBeginExecutionAsync), 0, "claimed, then the agent died before the action ran")]
    [InlineData(nameof(ICommandStore.SaveCommandAsync), 1, "recorded but never claimed: its first and only execution")]
    public async Task RealStore_CrashBetweenRecordAndExecute_NeverLeadsToADoubleExecution(
        string crashAfter, int expectedExecutions, string because)
    {
        using var db = new TempDb();
        ResultAccepted(true);
        var command = Command("c1") with { IssuedAt = null! };
        Poll(command);

        // First process: records the command, then "crashes" before executing it.
        var crashing = new CrashingStore(new SqliteCommandStore(db.Path)) { CrashAfter = crashAfter };
        await RealStoreWorker(crashing).RunCycleAsync(CancellationToken.None);
        Executions().Should().Be(0);

        // Restarted process; the portal replays the same command on every poll.
        var store = new SqliteCommandStore(db.Path);
        var restarted = RealStoreWorker(store);
        await restarted.RunCycleAsync(CancellationToken.None);
        await restarted.RunCycleAsync(CancellationToken.None);
        Executions().Should().Be(expectedExecutions, because);

        // Still not after the result retention removed the command row.
        await store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddMinutes(1));
        await RealStoreWorker(new SqliteCommandStore(db.Path)).RunCycleAsync(CancellationToken.None);
        Executions().Should().Be(expectedExecutions, because);
        store.GetCommandIdOutcome("c1").Should().Be(expectedExecutions == 0 ? "Failed" : "Completed");
    }
}