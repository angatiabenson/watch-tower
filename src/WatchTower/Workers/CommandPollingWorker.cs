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
        "Interrupted: the agent stopped while this command was running; its outcome is unknown";

    /// <summary>Result of a stored command that waited too long to be run (see <see cref="ResumeUnclaimedCommandsAsync"/>).</summary>
    internal const string ExpiredBeforeRunMessage =
        RejectedPrefix + "command expired before it could run (agent restarted)";

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

        await ResumeUnclaimedCommandsAsync(opts, ct);
        await PollAndExecuteCommandsAsync(opts, ct);
        await ExecuteDueScheduledCommandsAsync(opts, ct);
        await RetryUnsentResultsAsync(ct);
        await SettleUndeliverableResultsAsync(opts);
        await PurgeFinishedCommandsAsync(opts);
    }

    /// <summary>Results retried per cycle; due ones first, so a refused result cannot starve the rest.</summary>
    internal const int MaxResultsPerCycle = 50;

    /// <summary>
    /// Rejected commands kept at most (newest first). A portal that keeps sending invalid
    /// commands and refusing their results cannot grow the store without bound.
    /// </summary>
    internal const int DefaultMaxStoredRejections = 1_000;

    /// <summary>See <see cref="DefaultMaxStoredRejections"/>; lowered by tests.</summary>
    internal int MaxStoredRejections { get; init; } = DefaultMaxStoredRejections;

    /// <summary>
    /// Gives up on results that have waited longer than <see cref="CommandPollOptions.ResultRetryMaxHours"/>
    /// and drops the oldest rejections beyond <see cref="MaxStoredRejections"/>. Both are logged.
    /// </summary>
    private async Task SettleUndeliverableResultsAsync(PortalOptions opts)
    {
        var maxHours = Math.Max(1, opts.CommandPoll.ResultRetryMaxHours);
        try
        {
            foreach (var result in await _commandStore.AbandonUndeliveredResultsAsync(UtcNow - TimeSpan.FromHours(maxHours), UtcNow) ?? [])
            {
                _logger.LogWarning(
                    "Gave up delivering the result of command {CommandId}: the portal did not accept it in {Attempts} " +
                    "attempt(s) over {Hours} h (produced {ProducedAt})",
                    result.CommandId, result.Attempts, maxHours, result.ProducedAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to give up on undeliverable command results");
        }

        try
        {
            var trimmed = await _commandStore.TrimRejectionsAsync(MaxStoredRejections);
            if (trimmed is { Undelivered.Count: > 0 })
            {
                _logger.LogWarning(
                    "More than {Max} rejected commands are stored; dropped the {Count} oldest, including {Undelivered} " +
                    "whose result was never delivered (e.g. {CommandId})",
                    MaxStoredRejections, trimmed.Deleted, trimmed.Undelivered.Count, trimmed.Undelivered[0].CommandId);
            }
            else if (trimmed is { Deleted: > 0 })
            {
                _logger.LogInformation(
                    "More than {Max} rejected commands are stored; dropped the {Count} oldest", MaxStoredRejections, trimmed.Deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trim stored rejected commands");
        }
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

    /// <summary>
    /// Runs the commands that were stored ('Received') but never claimed: the agent stopped
    /// between saving and claiming one (crash, kill, reboot), or claiming it failed. Nothing
    /// else would run them: the portal may never re-deliver them, and a stale re-delivery is
    /// ignored because the id is known. Each goes through the normal path (freshness, then
    /// claim, execute and report), so it runs at most once; one too old to run now is rejected
    /// with <see cref="ExpiredBeforeRunMessage"/>. Deferred commands (with a ScheduledAt) are
    /// left to <see cref="ExecuteDueScheduledCommandsAsync"/>. Commands left 'Executing' are not
    /// here: they may have had effects and are failed by <see cref="RecoverInterruptedCommandsAsync"/>.
    /// Runs every cycle, before the poll; at that point no command of this process sits between
    /// being saved and being claimed.
    /// </summary>
    private async Task ResumeUnclaimedCommandsAsync(PortalOptions opts, CancellationToken ct)
    {
        IReadOnlyList<AgentCommandRecord> pending;
        try
        {
            pending = await _commandStore.GetPendingCommandsAsync() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the stored commands that were never run");
            return;
        }

        foreach (var record in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (TryParseUtc(record.ScheduledAt, out _))
                continue;

            // Isolated per command, like the poll path; one that fails is tried again next cycle.
            try
            {
                await ResumeUnclaimedCommandAsync(record, opts, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run stored command {CommandId} (ActionType={ActionType})",
                    record.CommandId, record.ActionType);
            }
        }
    }

    private async Task ResumeUnclaimedCommandAsync(AgentCommandRecord record, PortalOptions opts, CancellationToken ct)
    {
        if (!MayStillRun(record, opts, out var rejection))
        {
            var rejected = await _commandStore.TryRejectUnclaimedAsync(record.CommandId, rejection);
            if (rejected is null)
                return; // claimed or finished meanwhile

            _logger.LogWarning(
                "Rejecting stored command {CommandId} (ActionType={ActionType}, received {ReceivedAt}) that was never run: {Reason}",
                record.CommandId, record.ActionType, record.ReceivedAt, rejection);
            await ReportResultAsync(rejected, ct);
            return;
        }

        if (!TryReadCommand(record, out var command))
        {
            await FailCommandAsync(command, "Stored command parameters are unreadable", ct);
            return;
        }

        _logger.LogInformation(
            "Running command {CommandId} (ActionType={ActionType}): received {ReceivedAt} but not run before the agent stopped",
            record.CommandId, record.ActionType, record.ReceivedAt);
        await ExecuteCommandAsync(command, opts, ct);
    }

    /// <summary>
    /// The receipt freshness check (<see cref="IsFresh"/>), applied again now to a stored command
    /// that was never run. One without issuedAt is aged from when it was received, with the same
    /// limit (MaxCommandAgeSeconds + ClockSkewSeconds; none when MaxCommandAgeSeconds is 0).
    /// </summary>
    private bool MayStillRun(AgentCommandRecord record, PortalOptions opts, out string rejection)
    {
        rejection = string.Empty;
        var stored = new AgentCommandDto(record.CommandId, record.ActionType, new Dictionary<string, string>(),
            record.ScheduledAt, record.IssuedAt!);
        if (!IsFresh(stored, opts, out var reason, out var stale))
        {
            rejection = stale ? ExpiredBeforeRunMessage : $"{RejectedPrefix}{reason}";
            return false;
        }

        var maxAge = opts.CommandPoll.MaxCommandAgeSeconds;
        if (maxAge > 0 && string.IsNullOrWhiteSpace(record.IssuedAt) && TryParseUtc(record.ReceivedAt, out var received) &&
            UtcNow - received > TimeSpan.FromSeconds(maxAge) + TimeSpan.FromSeconds(opts.CommandPoll.ClockSkewSeconds))
        {
            rejection = ExpiredBeforeRunMessage;
            return false;
        }
        return true;
    }

    /// <summary>Rebuilds a stored command; <c>false</c> (with empty parameters) when its parameters are unreadable.</summary>
    private bool TryReadCommand(AgentCommandRecord record, out AgentCommandDto command)
    {
        Dictionary<string, string>? parameters = null;
        var readable = true;
        try
        {
            parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(record.ParamsJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Stored command {CommandId} has unreadable parameters", record.CommandId);
            readable = false;
        }

        command = new AgentCommandDto(
            record.CommandId,
            record.ActionType,
            parameters ?? new Dictionary<string, string>(),
            record.ScheduledAt,
            record.IssuedAt ?? record.ReceivedAt);
        return readable;
    }

    private static bool TryParseUtc(string? value, out DateTime utc)
    {
        utc = default;
        return !string.IsNullOrWhiteSpace(value) &&
               DateTime.TryParse(value, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    private async Task PurgeFinishedCommandsAsync(PortalOptions opts)
    {
        if (UtcNow - _lastPurge < PurgeInterval)
            return;

        _lastPurge = UtcNow;
        var retentionDays = opts.CommandPoll.RetentionDays;
        if (retentionDays > 0)
        {
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

        // The remembered ids outlive the command rows (replay protection) but are bounded too.
        try
        {
            DateTime? seenBefore = opts.CommandIdRetentionDays > 0
                ? UtcNow - TimeSpan.FromDays(opts.CommandIdRetentionDays)
                : null;
            var forgotten = await _commandStore.PruneCommandIdsAsync(seenBefore, opts.CommandIdMaxCount);
            if (forgotten > 0)
                _logger.LogInformation(
                    "Forgot {Count} command id(s) seen more than {Days} days ago or beyond the newest {Max}",
                    forgotten, opts.CommandIdRetentionDays, opts.CommandIdMaxCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune remembered command ids");
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
        // (recent IssuedAt, future ScheduledAt) still pass. A missing IssuedAt is accepted unless
        // VisibilityPortal:RequireCommandIssuedAt is set.
        if (!IsFresh(command, opts, out var freshnessReason, out _))
        {
            await RejectAsync(command, freshnessReason, ct);
            return;
        }

        // Records the id before anything runs; a known id is never stored (or executed) again,
        // even once its command row has been purged by the result retention.
        var pending = await _commandStore.SaveCommandAsync(command);

        // Acknowledge receipt immediately (fire-and-forget on failure). Re-deliveries are
        // acknowledged too: the portal may re-send a command because the first ack was lost.
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

        if (!pending)
        {
            _logger.LogDebug(
                "Command {CommandId} (ActionType={ActionType}) was received before; not executing it again",
                command.CommandId, command.ActionType);
            return;
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
                    if (!TryReadCommand(record, out var command))
                    {
                        await FailCommandAsync(command, "Stored command parameters are unreadable", ct);
                        continue;
                    }

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

    internal const string IssuedAtRequiredReason = "issuedAt is required";

    /// <param name="stale"><c>true</c> when the command was refused only for being too old.</param>
    private bool IsFresh(AgentCommandDto command, PortalOptions opts, out string reason, out bool stale)
    {
        reason = string.Empty;
        stale = false;
        if (opts.RequireCommandIssuedAt && string.IsNullOrWhiteSpace(command.IssuedAt))
        {
            reason = IssuedAtRequiredReason;
            return false;
        }

        var maxAge = opts.CommandPoll.MaxCommandAgeSeconds;
        if (maxAge <= 0)
            return true; // freshness check disabled

        if (string.IsNullOrWhiteSpace(command.IssuedAt))
        {
            // Portals written before issuedAt existed never send it. Accept such commands so they
            // keep working; the per-command-id duplicate check (remembered for
            // CommandIdRetentionDays) still applies, only the age check cannot. Warn once so the
            // operator knows the protection is reduced.
            if (!string.Equals(_missingIssuedAtWarnedFor, opts.BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _missingIssuedAtWarnedFor = opts.BaseUrl;
                _logger.LogWarning(MissingIssuedAtWarning);
            }
            return true;
        }

        if (!TryParseUtc(command.IssuedAt, out var issued))
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
            stale = true;
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
    /// picked up by <see cref="RetryUnsentResultsAsync"/> once its backoff has passed.
    /// </summary>
    private async Task<bool> ReportResultAsync(CommandResultDto result, CancellationToken ct, bool retry = false)
    {
        Exception? error = null;
        try
        {
            if (await _portalClient.ReportCommandResultAsync(result, ct))
            {
                await _commandStore.MarkResultSentAsync(result.CommandId);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = ex;
        }

        DateTime? next = null;
        try
        {
            next = await _commandStore.RecordResultAttemptFailedAsync(result.CommandId, UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to schedule the next attempt for the result of command {CommandId}", result.CommandId);
        }

        // Retries back off to one attempt per 30 minutes per result, so this stays quiet.
        _logger.Log(retry ? LogLevel.Debug : LogLevel.Warning, error,
            "Portal did not accept the result for command {CommandId}; next attempt at {NextAttempt}",
            result.CommandId, next?.ToString("O") ?? "the next cycle");
        return false;
    }

    private async Task RetryUnsentResultsAsync(CancellationToken ct)
    {
        IReadOnlyList<AgentCommandRecord> due;
        try
        {
            due = await _commandStore.GetDueResultsAsync(UtcNow, MaxResultsPerCycle) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the command results waiting for delivery");
            return;
        }

        foreach (var record in due)
        {
            if (ct.IsCancellationRequested) break;
            if (record.ResultJson == null) continue;

            CommandResultDto? result;
            try
            {
                result = JsonSerializer.Deserialize<CommandResultDto>(record.ResultJson);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Stored result of command {CommandId} is unreadable", record.CommandId);
                result = null;
            }

            if (result == null)
            {
                // Back off like a refused result; it is given up on after ResultRetryMaxHours.
                try { await _commandStore.RecordResultAttemptFailedAsync(record.CommandId, UtcNow); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to schedule the result of command {CommandId}", record.CommandId); }
                continue;
            }

            // Isolated per result: one that throws must not keep the others from being sent.
            await ReportResultAsync(result, ct, retry: true);
        }
    }
}