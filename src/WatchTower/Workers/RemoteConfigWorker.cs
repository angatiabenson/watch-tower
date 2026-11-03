using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Storage;

namespace WatchTower.Workers;

public class RemoteConfigWorker : BackgroundService
{
    private readonly IPortalClient _portalClient;
    private readonly IRemoteConfigStore _configStore;
    private readonly IConfigSyncTrigger _syncTrigger;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger<RemoteConfigWorker> _logger;

    public RemoteConfigWorker(
        IPortalClient portalClient,
        IRemoteConfigStore configStore,
        IConfigSyncTrigger syncTrigger,
        IOptionsMonitor<PortalOptions> options,
        ILogger<RemoteConfigWorker> logger)
    {
        _portalClient = portalClient;
        _configStore = configStore;
        _syncTrigger = syncTrigger;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Remote Config Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;

                if (opts.Enabled && opts.RemoteConfig.Enabled)
                {
                    await FetchAndApplyConfigAsync(stoppingToken);
                    await AcknowledgePendingAsync(stoppingToken);
                }

                var intervalSeconds = Math.Max(1, _options.CurrentValue.RemoteConfig.PollIntervalSeconds);

                // Check sync trigger (triggered by ForceConfigSync action)
                var triggered = _syncTrigger.TryConsume();
                if (!triggered)
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Remote Config Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Remote Config Worker stopped");
    }

    private async Task FetchAndApplyConfigAsync(CancellationToken ct)
    {
        string? currentVersion = null;
        try
        {
            currentVersion = await _configStore.GetCurrentVersionAsync();
            var (hasUpdate, config, version) =
                await _portalClient.FetchConfigurationAsync(currentVersion, ct);

            if (!hasUpdate || config == null || version == null)
                return;

            if (!RemoteConfigValidator.Validate(config, out var validationError))
            {
                _logger.LogWarning(
                    "Remote config version {Version} failed validation: {Error}", version, validationError);
                return;
            }

            await ApplyConfigurationHandler.WriteRemoteConfigFileAsync(config, ct);
            await _configStore.SaveConfigAsync(config, version);

            _logger.LogInformation("Remote configuration updated to version {Version}", version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to fetch remote config (current version: {Version})", currentVersion);
        }
    }

    private async Task AcknowledgePendingAsync(CancellationToken ct)
    {
        try
        {
            var (_, version) = await _configStore.GetUnacknowledgedAsync();
            if (version == null) return;

            var ok = await _portalClient.AcknowledgeConfigAsync(version, ct);
            if (ok)
                await _configStore.MarkAcknowledgedAsync(version);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AcknowledgePending encountered an error");
        }
    }

    private static bool ValidateConfig(
        Portal.Contracts.RemoteAgentConfigDto config, out string error)
    {
        error = string.Empty;

        if (config.Cpu?.PollIntervalSeconds < 1)
            return Fail(out error, "Cpu.PollIntervalSeconds must be >= 1");
        if (config.Cpu?.ThresholdPercent is < 1 or > 100)
            return Fail(out error, "Cpu.ThresholdPercent must be 1–100");
        if (config.Ram?.PollIntervalSeconds < 1)
            return Fail(out error, "Ram.PollIntervalSeconds must be >= 1");
        if (config.Ram?.ThresholdPercent is < 1 or > 100)
            return Fail(out error, "Ram.ThresholdPercent must be 1–100");
        if (config.Disk?.CriticalThresholdPercent is < 1 or > 100)
            return Fail(out error, "Disk.CriticalThresholdPercent must be 1–100");

        return true;
    }

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }
}