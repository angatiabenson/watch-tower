using System.Text.Json;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;

namespace WatchTower.Portal.Actions.Handlers;

public class ApplyConfigurationHandler : IActionHandler
{
    /// <summary>The result when the operator turned off portal-managed settings.</summary>
    public const string DisabledMessage =
        "Rejected: this server does not let the portal manage its settings (VisibilityPortal:RemoteConfig:Enabled is false)";

    private readonly IRemoteConfigStore _configStore;
    private readonly WatchTowerPaths _paths;
    private readonly IOptionsMonitor<PortalOptions> _portal;
    private readonly ILogger<ApplyConfigurationHandler> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ActionType ActionType => ActionType.ApplyConfiguration;

    public ApplyConfigurationHandler(
        IRemoteConfigStore configStore,
        WatchTowerPaths paths,
        IOptionsMonitor<PortalOptions> portal,
        ILogger<ApplyConfigurationHandler> logger)
    {
        _configStore = configStore;
        _paths = paths;
        _portal = portal;
        _logger = logger;
    }

    public async Task<ActionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        // "Allow the portal to manage settings" covers both ways the portal can push settings:
        // config polling and this command.
        if (!_portal.CurrentValue.RemoteConfig.Enabled)
        {
            _logger.LogWarning("ApplyConfiguration refused: the portal may not manage this server's settings");
            return new ActionResult(false, DisabledMessage);
        }

        if (!parameters.TryGetValue("ConfigJson", out var configJson) ||
            string.IsNullOrWhiteSpace(configJson))
            return new ActionResult(false, "Missing ConfigJson parameter");

        RemoteAgentConfigDto? config;
        try
        {
            config = JsonSerializer.Deserialize<RemoteAgentConfigDto>(configJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ApplyConfiguration: invalid ConfigJson");
            return new ActionResult(false, $"Invalid ConfigJson: {ex.Message}");
        }

        if (config == null)
            return new ActionResult(false, "ConfigJson deserialised to null");

        if (!RemoteConfigValidator.Validate(config, out var validationError))
        {
            _logger.LogWarning("ApplyConfiguration rejected: {Error}", validationError);
            return new ActionResult(false, validationError);
        }

        var version = parameters.TryGetValue("Version", out var v) ? v : Guid.NewGuid().ToString();

        // Write the overlay first: only record the version once it is actually in effect.
        await WriteRemoteConfigFileAsync(_paths.RemoteConfigFilePath, config, ct);
        await _configStore.SaveConfigAsync(config, version);

        _logger.LogInformation("Remote configuration applied (version {Version})", version);
        return new ActionResult(true, $"Configuration applied (version {version})");
    }

    internal static async Task WriteRemoteConfigFileAsync(string filePath, RemoteAgentConfigDto config, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var tempPath = filePath + ".tmp";

        var json = BuildConfigFileJson(config);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, filePath, overwrite: true);
    }

    internal static string BuildConfigFileJson(RemoteAgentConfigDto config)
    {
        var monitoring = new Dictionary<string, object?>();

        if (config.Cpu != null)
        {
            var cpu = new Dictionary<string, object?>();
            if (config.Cpu.PollIntervalSeconds.HasValue) cpu["PollIntervalSeconds"] = config.Cpu.PollIntervalSeconds;
            if (config.Cpu.ThresholdPercent.HasValue) cpu["ThresholdPercent"] = config.Cpu.ThresholdPercent;
            if (config.Cpu.SustainedSeconds.HasValue) cpu["SustainedSeconds"] = config.Cpu.SustainedSeconds;
            if (cpu.Count > 0) monitoring["Cpu"] = cpu;
        }

        if (config.Ram != null)
        {
            var ram = new Dictionary<string, object?>();
            if (config.Ram.PollIntervalSeconds.HasValue) ram["PollIntervalSeconds"] = config.Ram.PollIntervalSeconds;
            if (config.Ram.ThresholdPercent.HasValue) ram["ThresholdPercent"] = config.Ram.ThresholdPercent;
            if (config.Ram.SustainedSeconds.HasValue) ram["SustainedSeconds"] = config.Ram.SustainedSeconds;
            if (ram.Count > 0) monitoring["Ram"] = ram;
        }

        if (config.Disk != null)
        {
            var disk = new Dictionary<string, object?>();
            if (config.Disk.PollIntervalSeconds.HasValue) disk["PollIntervalSeconds"] = config.Disk.PollIntervalSeconds;
            if (config.Disk.WarningThresholdPercent.HasValue) disk["WarningThresholdPercent"] = config.Disk.WarningThresholdPercent;
            if (config.Disk.CriticalThresholdPercent.HasValue) disk["CriticalThresholdPercent"] = config.Disk.CriticalThresholdPercent;
            if (config.Disk.QueueLengthWarningThreshold.HasValue) disk["QueueLengthWarningThreshold"] = config.Disk.QueueLengthWarningThreshold;
            if (disk.Count > 0) monitoring["Disk"] = disk;
        }

        if (config.Network != null)
        {
            var net = new Dictionary<string, object?>();
            if (config.Network.PollIntervalSeconds.HasValue) net["PollIntervalSeconds"] = config.Network.PollIntervalSeconds;
            if (config.Network.OutputQueueWarningThreshold.HasValue) net["OutputQueueWarningThreshold"] = config.Network.OutputQueueWarningThreshold;
            if (net.Count > 0) monitoring["Network"] = net;
        }

        if (config.Services != null)
        {
            var svc = new Dictionary<string, object?>();
            if (config.Services.PollIntervalSeconds.HasValue) svc["PollIntervalSeconds"] = config.Services.PollIntervalSeconds;
            if (config.Services.WatchedServices != null) svc["WatchedServices"] = config.Services.WatchedServices;
            if (svc.Count > 0) monitoring["Services"] = svc;
        }

        if (config.AppPools != null)
        {
            var pools = new Dictionary<string, object?>();
            if (config.AppPools.PollIntervalSeconds.HasValue) pools["PollIntervalSeconds"] = config.AppPools.PollIntervalSeconds;
            if (config.AppPools.WatchedApplicationPools != null) pools["WatchedApplicationPools"] = config.AppPools.WatchedApplicationPools;
            if (pools.Count > 0) monitoring["ApplicationPools"] = pools;
        }

        var root = new Dictionary<string, object?>();
        if (monitoring.Count > 0) root["Monitoring"] = monitoring;

        if (config.Portal != null)
        {
            var portal = new Dictionary<string, object?>();
            var outbox = new Dictionary<string, object?>();
            var commandPoll = new Dictionary<string, object?>();
            var remoteConfig = new Dictionary<string, object?>();

            if (config.Portal.OutboxFlushIntervalSeconds.HasValue)
                outbox["FlushIntervalSeconds"] = config.Portal.OutboxFlushIntervalSeconds;
            if (config.Portal.CommandPollIntervalSeconds.HasValue)
                commandPoll["PollIntervalSeconds"] = config.Portal.CommandPollIntervalSeconds;
            if (config.Portal.RemoteConfigPollIntervalSeconds.HasValue)
                remoteConfig["PollIntervalSeconds"] = config.Portal.RemoteConfigPollIntervalSeconds;

            if (outbox.Count > 0) portal["Outbox"] = outbox;
            if (commandPoll.Count > 0) portal["CommandPoll"] = commandPoll;
            if (remoteConfig.Count > 0) portal["RemoteConfig"] = remoteConfig;
            if (portal.Count > 0) root["VisibilityPortal"] = portal;
        }

        if (config.Maintenance != null)
        {
            var maintenance = new Dictionary<string, object?>();
            if (config.Maintenance.UntilUtc != null &&
                Services.Alerting.MaintenanceState.TryParseUntil(config.Maintenance.UntilUtc, out var until))
                maintenance["Until"] = Services.Alerting.MaintenanceState.Format(until);
            if (config.Maintenance.Reason != null) maintenance["Reason"] = config.Maintenance.Reason.Trim();
            if (maintenance.Count > 0) root["Maintenance"] = maintenance;
        }

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}