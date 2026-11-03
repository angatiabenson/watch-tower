using System.Globalization;
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
    private readonly IOptionsMonitor<ServiceMonitorOptions> _serviceOptions;
    private readonly IOptionsMonitor<ApplicationPoolMonitorOptions> _appPoolOptions;
    private readonly ILogger<CommandPollingWorker> _logger;

    public CommandPollingWorker(
        IPortalClient portalClient,
        ICommandStore commandStore,
        ActionHandlerRegistry registry,
        IOptionsMonitor<PortalOptions> options,
        IOptionsMonitor<ServiceMonitorOptions> serviceOptions,
        IOptionsMonitor<ApplicationPoolMonitorOptions> appPoolOptions,
        ILogger<CommandPollingWorker> logger)
    {
        _portalClient = portalClient;
        _commandStore = commandStore;
        _registry = registry;
        _options = options;
        _serviceOptions = serviceOptions;
        _appPoolOptions = appPoolOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command Polling Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);

                var pollInterval = Math.Max(1, _options.CurrentValue.CommandPoll.PollIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(pollInterval), stoppingToken);
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

    /// <summary>One poll / execute-due / retry-results cycle. Exposed for deterministic testing.</summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || opts.CommandPoll.PollIntervalSeconds <= 0)
            return;

        await PollAndExecuteCommandsAsync(opts, ct);
        await ExecuteDueScheduledCommandsAsync(opts, ct);
        await RetryUnsentResultsAsync(ct);
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

            // Replay protection: reject stale commands at receipt (before persisting).
            // Validated on IssuedAt, not ScheduledAt, so legitimately deferred commands
            // (recent IssuedAt, future ScheduledAt) still pass.
            if (!IsFresh(command, opts, out var freshnessReason))
            {
                _logger.LogWarning(
                    "Rejecting command {CommandId} (ActionType={ActionType}): {Reason}",
                    command.CommandId, command.ActionType, freshnessReason);
                continue;
            }

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

            // Check scheduled execution (parse as UTC regardless of portal formatting)
            if (!string.IsNullOrEmpty(command.ScheduledAt) &&
                DateTime.TryParse(command.ScheduledAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var scheduled) &&
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
        // Authorization: state-changing service/app-pool actions may only target names the
        // operator has explicitly placed under WatchTower's watch. This bounds the blast
        // radius of a compromised/buggy portal to resources already in scope.
        if (!IsAuthorized(command, out var authReason))
        {
            _logger.LogWarning(
                "Refusing unauthorized command {CommandId} (ActionType={ActionType}): {Reason}",
                command.CommandId, command.ActionType, authReason);
            await FailCommandAsync(command, $"Not authorized: {authReason}", ct);
            return;
        }

        // Atomically claim the command so the poll path and the scheduled-due path cannot
        // both execute it. If we did not win the claim, another path already handled it.
        if (!await _commandStore.TryBeginExecutionAsync(command.CommandId))
        {
            _logger.LogDebug("Command {CommandId} already claimed; skipping", command.CommandId);
            return;
        }

        _logger.LogInformation(
            "Executing command {CommandId} ActionType={ActionType}",
            command.CommandId, command.ActionType);

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

        // Send the result; it stays stored (result_json) and is retried by RetryUnsentResultsAsync
        // until the portal confirms receipt.
        await ReportResultAsync(result, ct);
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

    private bool IsFresh(AgentCommandDto command, PortalOptions opts, out string reason)
    {
        reason = string.Empty;
        var maxAge = opts.CommandPoll.MaxCommandAgeSeconds;
        if (maxAge <= 0)
            return true; // freshness check disabled

        if (string.IsNullOrWhiteSpace(command.IssuedAt))
            return true; // cannot validate without an issue time

        if (!DateTime.TryParse(command.IssuedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var issued))
        {
            reason = $"unparseable IssuedAt '{command.IssuedAt}'";
            return false;
        }

        var now = DateTime.UtcNow;
        var skew = TimeSpan.FromSeconds(opts.CommandPoll.ClockSkewSeconds);

        if (issued > now + skew)
        {
            reason = $"IssuedAt is in the future ({issued:O})";
            return false;
        }
        if (now - issued > TimeSpan.FromSeconds(maxAge) + skew)
        {
            reason = $"command is stale (issued {issued:O}, max age {maxAge}s)";
            return false;
        }
        return true;
    }

    private bool IsAuthorized(AgentCommandDto command, out string reason)
    {
        reason = string.Empty;
        switch ((Portal.Actions.ActionType)command.ActionType)
        {
            case Portal.Actions.ActionType.ServiceStart:
            case Portal.Actions.ActionType.ServiceStop:
            case Portal.Actions.ActionType.ServiceRestart:
                return IsTargetAllowed(command, "ServiceName",
                    _serviceOptions.CurrentValue.WatchedServices, "watched service", out reason);

            case Portal.Actions.ActionType.AppPoolStart:
            case Portal.Actions.ActionType.AppPoolStop:
            case Portal.Actions.ActionType.AppPoolRestart:
            case Portal.Actions.ActionType.AppPoolRecycle:
                return IsTargetAllowed(command, "AppPoolName",
                    _appPoolOptions.CurrentValue.WatchedApplicationPools, "watched application pool", out reason);

            default:
                // Agent/config actions are not target-scoped; allowed.
                return true;
        }
    }

    private static bool IsTargetAllowed(
        AgentCommandDto command, string paramKey, IReadOnlyList<string> allowList, string label, out string reason)
    {
        reason = string.Empty;
        if (!command.Parameters.TryGetValue(paramKey, out var target) || string.IsNullOrWhiteSpace(target))
        {
            reason = $"missing {paramKey}";
            return false;
        }
        target = target.Trim();
        if (allowList.Any(s => string.Equals(s?.Trim(), target, StringComparison.OrdinalIgnoreCase)))
            return true;

        reason = $"'{target}' is not a {label}";
        return false;
    }

    private async Task FailCommandAsync(AgentCommandDto command, string message, CancellationToken ct)
    {
        await _commandStore.MarkFailedAsync(command.CommandId, message);
        var result = new CommandResultDto(command.CommandId, false, message, DateTime.UtcNow.ToString("O"));
        await ReportResultAsync(result, ct);
    }

    /// <summary>
    /// Reports a command result and marks it sent only when the portal confirms receipt.
    /// <see cref="IPortalClient.ReportCommandResultAsync"/> signals failure by returning
    /// <c>false</c> (it does not throw), so the result must stay pending in that case and be
    /// picked up by <see cref="RetryUnsentResultsAsync"/>.
    /// </summary>
    private async Task<bool> ReportResultAsync(CommandResultDto result, CancellationToken ct)
    {
        try
        {
            if (await _portalClient.ReportCommandResultAsync(result, ct))
            {
                await _commandStore.MarkResultSentAsync(result.CommandId);
                return true;
            }

            _logger.LogWarning(
                "Portal did not accept the result for command {CommandId}; will retry", result.CommandId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Failed to send result for command {CommandId}, will retry", result.CommandId);
        }
        return false;
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