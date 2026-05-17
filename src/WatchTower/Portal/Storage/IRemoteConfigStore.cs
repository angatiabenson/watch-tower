using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Storage;

public interface IRemoteConfigStore
{
    Task SaveConfigAsync(RemoteAgentConfigDto config, string version);
    Task<string?> GetCurrentVersionAsync();
    Task MarkAcknowledgedAsync(string version);
    Task<(RemoteAgentConfigDto? Config, string? Version)> GetUnacknowledgedAsync();
}
