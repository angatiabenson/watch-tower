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

        _store.Setup(s => s.TryBeginExecutionAsync(It.IsAny<string>())).ReturnsAsync(true);
        _store.Setup(s => s.TryRecordRejectedAsync(It.IsAny<AgentCommandDto>(), It.IsAny<string>())).ReturnsAsync(true);
        _store.Setup(s => s.GetDueScheduledCommandsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        _store.Setup(s => s.GetUnsentResultsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
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
        _store.Setup(s => s.GetUnsentResultsAsync()).ReturnsAsync(new[]
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

            // 2. Portal back, same stale command re-delivered: no second rejection; the stored one is retried.
            ResultAccepted(true);
            await worker.RunCycleAsync(CancellationToken.None);
            (await store.GetUnsentResultsAsync()).Should().BeEmpty();
            _portal.Verify(p => p.ReportCommandResultAsync(
                It.Is<CommandResultDto>(r => r.CommandId == "stale" && !r.Success && r.Message.StartsWith("Rejected: command is stale")),
                It.IsAny<CancellationToken>()), Times.Exactly(3), "sent at rejection and by the retry of both cycles");

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
}