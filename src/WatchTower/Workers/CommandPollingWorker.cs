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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CommandPollingWorker> _logger;
    private readonly ICommandTargetPolicy? _targetPolicy;

    /// <summary>How often finished command rows older than the retention window are purged.</summary>
    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    internal const string InterruptedMessage =
        "Agent stopped while the command was executing; its outcome is unknown";

    private bool _interruptedRecovered;
    private DateTime _lastPurge = DateTime.MinValue;

    public CommandPollingWorker(
        IPortalClient portalClient,
        ICommandStore commandStore,
        ActionHandlerRegistry registry,
        IOptionsMonitor<PortalOptions> options,
        IOptionsMonitor<ServiceMonitorOptions> serviceOptions,
        IOptionsMonitor<ApplicationPoolMonitorOptions> appPoolOptions,
        TimeProvider timeProvider,
        ILogger<CommandPollingWorker> logger,
        ICommandTargetPolicy? targetPolicy = null)
    {
        _targetPolicy = targetPolicy;
        _portalClient = portalClient;
        _commandStore = commandStore;
        _registry = registry;
        _options = options;
        _serviceOptions = serviceOptions;
        _appPoolOptions = appPoolOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command Polling Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);

                var pollInterval = Math.Max(1, _options.CurrentValue.CommandPoll.PollIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(pollInterval), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Command Polling Worker");
                await Task.Delay(TimeSpan.FromSeconds(15), _timeProvider, stoppingToken);
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

        if (!_interruptedRecovered)
        {
            await RecoverInterruptedCommandsAsync(ct);
            _interruptedRecovered = true;
        }

        await PollAndExecuteCommandsAsync(opts, ct);
        await ExecuteDueScheduledCommandsAsync(opts, ct);
        await RetryUnsentResultsAsync(ct);
        await PurgeFinishedCommandsAsync(opts);
    }

    /// <summary>
    /// Commands left in 'Executing' by a previous run (crash, kill, reboot mid-command) would
    /// otherwise stay there forever and the portal would never learn their fate. Fail them with
    /// an explicit message and report that result. Runs once, before this process executes anything.
    /// </summary>
    private async Task RecoverInterruptedCommandsAsync(CancellationToken ct)
    {
        IReadOnlyList<CommandResultDto> interrupted;
        try
        {
            interrupted = await _commandStore.FailInterruptedCommandsAsync(InterruptedMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recover commands interrupted by a previous shutdown");
            return;
        }

        foreach (var result in interrupted)
        {
            if (ct.IsCancellationRequested) break;
            _logger.LogWarning(
                "Command {CommandId} was still executing when the agent stopped; marked failed", result.CommandId);
            await ReportResultAsync(result, ct);
        }
    }

    private async Task PurgeFinishedCommandsAsync(PortalOptions opts)
    {
        var retentionDays = opts.CommandPoll.RetentionDays;
        if (retentionDays <= 0 || UtcNow - _lastPurge < PurgeInterval)
            return;

        _lastPurge = UtcNow;
        try
        {
            var purged = await _commandStore.PurgeFinishedCommandsAsync(UtcNow - TimeSpan.FromDays(retentionDays));
            if (purged > 0)
                _logger.LogInformation("Purged {Count} finished command(s) older than {Days} days", purged, retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge finished commands");
        }
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

        foreach (var received in commands)
        {
            if (ct.IsCancellationRequested) break;
            if (received == null) continue;

            // Each command is isolated: a malformed or throwing command must not abort the rest
            // of the batch (they would otherwise only be retried on the next poll, if at all).
            try
            {
                await ReceiveCommandAsync(received, opts, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process command {CommandId} (ActionType={ActionType})",
                    received.CommandId, received.ActionType);
            }
        }
    }

    private async Task ReceiveCommandAsync(AgentCommandDto received, PortalOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(received.CommandId))
        {
            _logger.LogWarning("Ignoring command without a CommandId (ActionType={ActionType})", received.ActionType);
            return;
        }

        // A portal may omit Parameters (JSON null); treat that as "no parameters".
        var command = received.Parameters == null
            ? received with { Parameters = new Dictionary<string, string>() }
            : received;

        // Replay protection: reject stale or future commands at receipt, before they are stored
        // for execution or acknowledged; the portal gets a failed result with the reason.
        // Validated on IssuedAt, not ScheduledAt, so legitimately deferred commands
        // (recent IssuedAt, future ScheduledAt) still pass. A missing IssuedAt is accepted.
        if (!IsFresh(command, opts, out var freshnessReason))
        {
            await RejectAsync(command, freshnessReason, ct);
            return;
        }

        // INSERT OR IGNORE: duplicate delivery is silently discarded
        await _commandStore.SaveCommandAsync(command);

        // Acknowledge receipt immediately (fire-and-forget on failure)
        try
        {
            var ack = new AcknowledgeCommandDto(command.CommandId, UtcNow.ToString("O"));
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
            scheduled > UtcNow.AddSeconds(10))
        {
            _logger.LogInformation(
                "Command {CommandId} scheduled for {ScheduledAt}, deferring",
                command.CommandId, scheduled);
            return;
        }

        await ExecuteCommandAsync(command, opts, ct);
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

        var executedAt = UtcNow;
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

                // Isolated per command, like the poll path: one bad record must not block the rest.
                try
                {
                    Dictionary<string, string>? parameters;
                    try
                    {
                        parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(record.ParamsJson);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Scheduled command {CommandId} has unreadable parameters", record.CommandId);
                        var unreadable = new AgentCommandDto(record.CommandId, record.ActionType,
                            new Dictionary<string, string>(), record.ScheduledAt, record.IssuedAt ?? record.ReceivedAt);
                        await FailCommandAsync(unreadable, "Stored command parameters are unreadable", ct);
                        continue;
                    }

                    var command = new AgentCommandDto(
                        record.CommandId,
                        record.ActionType,
                        parameters ?? new Dictionary<string, string>(),
                        record.ScheduledAt,
                        record.IssuedAt ?? record.ReceivedAt);

                    await ExecuteCommandAsync(command, opts, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute scheduled command {CommandId}", record.CommandId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error executing due scheduled commands");
        }
    }

    /// <summary>
    /// Refuses a command at receipt: it is stored as failed and its result ("Rejected: reason")
    /// is reported (and retried until accepted) like any refused command. It is not
    /// acknowledged, since it was never accepted. A command id the agent already knows is left
    /// alone: re-delivery of a command that was executed earlier must not report a failure.
    /// </summary>
    private async Task RejectAsync(AgentCommandDto command, string reason, CancellationToken ct)
    {
        if (!await _commandStore.TryRecordRejectedAsync(command, $"{RejectedPrefix}{reason}"))
        {
            _logger.LogDebug(
                "Ignoring re-delivered command {CommandId} (ActionType={ActionType}): {Reason}; it was handled before",
                command.CommandId, command.ActionType, reason);
            return;
        }

        _logger.LogWarning(
            "Rejecting command {CommandId} (ActionType={ActionType}): {Reason}",
            command.CommandId, command.ActionType, reason);
        await ReportResultAsync(
            new CommandResultDto(command.CommandId, false, $"{RejectedPrefix}{reason}", UtcNow.ToString("O")), ct);
    }

    internal const string RejectedPrefix = "Rejected: ";

    internal const string MissingIssuedAtWarning =
        "Portal sends commands without issuedAt; replay protection is reduced. Update the portal.";

    /// <summary>
    /// BaseUrl of the portal session the missing-issuedAt warning was logged for: once per
    /// session (process lifetime, or until the BaseUrl changes), not once per command.
    /// </summary>
    private string? _missingIssuedAtWarnedFor;

    private bool IsFresh(AgentCommandDto command, PortalOptions opts, out string reason)
    {
        reason = string.Empty;
        var maxAge = opts.CommandPoll.MaxCommandAgeSeconds;
        if (maxAge <= 0)
            return true; // freshness check disabled

        if (string.IsNullOrWhiteSpace(command.IssuedAt))
        {
            // Portals written before issuedAt existed never send it. Accept such commands so they
            // keep working; the per-command-id duplicate check still applies, only the age check
            // cannot. Warn once so the operator knows the protection is reduced.
            if (!string.Equals(_missingIssuedAtWarnedFor, opts.BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _missingIssuedAtWarnedFor = opts.BaseUrl;
                _logger.LogWarning(MissingIssuedAtWarning);
            }
            return true;
        }

        if (!DateTime.TryParse(command.IssuedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var issued))
        {
            reason = $"unparseable issuedAt '{command.IssuedAt}'";
            return false;
        }

        var now = UtcNow;
        var skewSeconds = opts.CommandPoll.ClockSkewSeconds;
        var skew = TimeSpan.FromSeconds(skewSeconds);

        if (issued > now + skew)
        {
            reason = $"issuedAt {issued:O} is more than {skewSeconds}s in the future (agent time {now:O})";
            return false;
        }
        if (now - issued > TimeSpan.FromSeconds(maxAge) + skew)
        {
            reason = $"command is stale: issuedAt {issued:O} is more than {maxAge + skewSeconds}s old " +
                     $"(max age {maxAge}s + {skewSeconds}s clock skew; agent time {now:O})";
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
                if (command.Parameters?.GetValueOrDefault("ServiceName") is { } serviceName &&
                    _targetPolicy?.DenyService(serviceName) is { } denied)
                {
                    reason = denied;
                    return false;
                }
                return IsTargetAllowed(command, "ServiceName",
                    _targetPolicy?.AllowedServices() ?? _serviceOptions.CurrentValue.WatchedServices,
                    "locally watched service", out reason);

            case Portal.Actions.ActionType.AppPoolStart:
            case Portal.Actions.ActionType.AppPoolStop:
            case Portal.Actions.ActionType.AppPoolRestart:
            case Portal.Actions.ActionType.AppPoolRecycle:
                return IsTargetAllowed(command, "AppPoolName",
                    _targetPolicy?.AllowedApplicationPools() ?? _appPoolOptions.CurrentValue.WatchedApplicationPools,
                    "locally watched application pool", out reason);

            default:
                // Agent/config actions are not target-scoped; allowed.
                return true;
        }
    }

    private static bool IsTargetAllowed(
        AgentCommandDto command, string paramKey, IReadOnlyList<string> allowList, string label, out string reason)
    {
        reason = string.Empty;
        if (command.Parameters is null || !command.Parameters.TryGetValue(paramKey, out var target) ||
            string.IsNullOrWhiteSpace(target))
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
        var result = new CommandResultDto(command.CommandId, false, message, UtcNow.ToString("O"));
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