using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Storage;

namespace WatchTower.Workers;

public class RemoteConfigWorker : BackgroundService
{
    private readonly IPortalClient _portalClient;
    private readonly IRemoteConfigStore _configStore;
    private readonly WatchTowerPaths _paths;
    private readonly IConfigSyncTrigger _syncTrigger;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoteConfigWorker> _logger;

    public RemoteConfigWorker(
        IPortalClient portalClient,
        IRemoteConfigStore configStore,
        WatchTowerPaths paths,
        IConfigSyncTrigger syncTrigger,
        IOptionsMonitor<PortalOptions> options,
        TimeProvider timeProvider,
        ILogger<RemoteConfigWorker> logger)
    {
        _portalClient = portalClient;
        _configStore = configStore;
        _paths = paths;
        _syncTrigger = syncTrigger;
        _options = options;
        _timeProvider = timeProvider;
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

                // Sleep for the poll interval, but wake immediately on ForceConfigSync. The
                // trigger used to be checked only after a full interval had already elapsed.
                if (await _syncTrigger.WaitAsync(TimeSpan.FromSeconds(intervalSeconds), _timeProvider, stoppingToken))
                    _logger.LogInformation("Configuration sync requested by the portal; fetching now");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Remote Config Worker");
                await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, stoppingToken);
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

            // The operator may have turned off portal-managed settings (or the portal) while the
            // fetch was in flight: re-read the live options right before writing the overlay.
            var live = _options.CurrentValue;
            if (!live.Enabled || !live.RemoteConfig.Enabled)
            {
                _logger.LogInformation(
                    "Remote config version {Version} was not applied: portal-managed settings were turned off during the fetch",
                    version);
                return;
            }

            await ApplyConfigurationHandler.WriteRemoteConfigFileAsync(_paths.RemoteConfigFilePath, config, ct);
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
}