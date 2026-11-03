using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
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

    public CommandPollingWorkerTests()
    {
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
        _store.Setup(s => s.GetDueScheduledCommandsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        _store.Setup(s => s.GetUnsentResultsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        _portal.Setup(p => p.AcknowledgeCommandAsync(It.IsAny<AcknowledgeCommandDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private static AgentCommandDto Command(string id, ActionType type = ActionType.ForceConfigSync,
        Dictionary<string, string>? parameters = null) =>
        new(id, (int)type, parameters ?? new Dictionary<string, string>(), null, DateTime.UtcNow.ToString("O"));

    private void Poll(params AgentCommandDto[] commands) =>
        _portal.Setup(p => p.PollCommandsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(commands);

    private void ResultAccepted(bool accepted) =>
        _portal.Setup(p => p.ReportCommandResultAsync(It.IsAny<CommandResultDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted);

    private CommandPollingWorker CreateWorker() => new(
        _portal.Object,
        _store.Object,
        new ActionHandlerRegistry(new[] { _handler.Object }, NullLogger<ActionHandlerRegistry>.Instance),
        _options.Object,
        _serviceOptions.Object,
        _poolOptions.Object,
        NullLogger<CommandPollingWorker>.Instance);

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
}