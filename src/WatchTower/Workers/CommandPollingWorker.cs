using System.Text.Json;
using Microsoft.Extensions.Options;
using WatchTower.Configuration;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;

namespace WatchTower.Workers;

public class CommandPollingWorker : BackgroundService
{
    private readonly IPortalClient _portalClient;
    private readonly ICommandStore _commandStore;
    private readonly ActionHandlerRegistry _registry;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger<CommandPollingWorker> _logger;

    public CommandPollingWorker(
        IPortalClient portalClient,
        ICommandStore commandStore,
        ActionHandlerRegistry registry,
        IOptionsMonitor<PortalOptions> options,
        ILogger<CommandPollingWorker> logger)
    {
        _portalClient = portalClient;
        _commandStore = commandStore;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command Polling Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;

                if (opts.Enabled && opts.CommandPoll.PollIntervalSeconds > 0)
                {
                    await PollAndExecuteCommandsAsync(opts, stoppingToken);
                    await ExecuteDueScheduledCommandsAsync(opts, stoppingToken);
                    await RetryUnsentResultsAsync(stoppingToken);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.CurrentValue.CommandPoll.PollIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Command Polling Worker");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        _logger.LogInformation("Command Polling Worker stopped");
    }

    private async Task PollAndExecuteCommandsAsync(PortalOptions opts, CancellationToken ct)
    {
        IReadOnlyList<AgentCommandDto> commands;
        try
        {
            commands = await _portalClient.PollCommandsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to poll commands from portal");
            return;
        }

        if (commands.Count == 0) return;

        _logger.LogInformation("Received {Count} command(s) from portal", commands.Count);

        foreach (var command in commands)
        {
            if (ct.IsCancellationRequested) break;

            // INSERT OR IGNORE: duplicate delivery is silently discarded
            await _commandStore.SaveCommandAsync(command);

            // Acknowledge receipt immediately (fire-and-forget on failure)
            try
            {
                var ack = new AcknowledgeCommandDto(command.CommandId, DateTime.UtcNow.ToString("O"));
                await _portalClient.AcknowledgeCommandAsync(ack, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to acknowledge command {CommandId} to portal (non-fatal)",
                    command.CommandId);
            }

            // Check scheduled execution
            if (!string.IsNullOrEmpty(command.ScheduledAt) &&
                DateTime.TryParse(command.ScheduledAt, out var scheduled) &&
                scheduled > DateTime.UtcNow.AddSeconds(10))
            {
                _logger.LogInformation(
                    "Command {CommandId} scheduled for {ScheduledAt}, deferring",
                    command.CommandId, scheduled);
                continue;
            }

            await ExecuteCommandAsync(command, opts, ct);
        }
    }

    private async Task ExecuteCommandAsync(
        AgentCommandDto command, PortalOptions opts, CancellationToken ct)
    {
        _logger.LogInformation(
            "Executing command {CommandId} ActionType={ActionType}",
            command.CommandId, command.ActionType);

        await _commandStore.MarkExecutingAsync(command.CommandId);

        var executedAt = DateTime.UtcNow;
        Portal.Actions.ActionResult actionResult;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(opts.CommandPoll.ExecutionTimeoutSeconds));

        try
        {
            actionResult = await _registry.ExecuteAsync(
                command.ActionType,
                command.Parameters,
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            actionResult = new Portal.Actions.ActionResult(false,
                $"Command timed out after {opts.CommandPoll.ExecutionTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandId} threw unexpected exception", command.CommandId);
            actionResult = new Portal.Actions.ActionResult(false, $"Unexpected error: {ex.Message}");
        }

        var result = new CommandResultDto(
            command.CommandId,
            actionResult.Success,
            actionResult.Message,
            executedAt.ToString("O")
        );

        if (actionResult.Success)
        {
            _logger.LogInformation(
                "Command {CommandId} completed: {Message}", command.CommandId, actionResult.Message);
            await _commandStore.MarkCompletedAsync(command.CommandId, result);
        }
        else
        {
            _logger.LogWarning(
                "Command {CommandId} failed: {Message}", command.CommandId, actionResult.Message);
            await _commandStore.MarkFailedAsync(command.CommandId, actionResult.Message);
        }

        // Send result to portal; store for retry if it fails
        try
        {
            await _portalClient.ReportCommandResultAsync(result, ct);
            await _commandStore.MarkResultSentAsync(command.CommandId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send result for command {CommandId}, will retry", command.CommandId);
        }
    }

    private async Task ExecuteDueScheduledCommandsAsync(PortalOptions opts, CancellationToken ct)
    {
        try
        {
            var due = await _commandStore.GetDueScheduledCommandsAsync();
            if (due.Count == 0) return;

            _logger.LogInformation("Executing {Count} due scheduled command(s)", due.Count);

            foreach (var record in due)
            {
                if (ct.IsCancellationRequested) break;

                var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(record.ParamsJson)
                    ?? new Dictionary<string, string>();

                var command = new AgentCommandDto(
                    record.CommandId,
                    record.ActionType,
                    parameters,
                    record.ScheduledAt,
                    record.IssuedAt ?? record.ReceivedAt);

                await ExecuteCommandAsync(command, opts, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error executing due scheduled commands");
        }
    }

    private async Task RetryUnsentResultsAsync(CancellationToken ct)
    {
        try
        {
            var unsent = await _commandStore.GetUnsentResultsAsync();
            foreach (var record in unsent)
            {
                if (ct.IsCancellationRequested) break;
                if (record.ResultJson == null) continue;

                var result = System.Text.Json.JsonSerializer.Deserialize<CommandResultDto>(
                    record.ResultJson);
                if (result == null) continue;

                var sent = await _portalClient.ReportCommandResultAsync(result, ct);
                if (sent)
                    await _commandStore.MarkResultSentAsync(record.CommandId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RetryUnsentResults encountered an error");
        }
    }
}
