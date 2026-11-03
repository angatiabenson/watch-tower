using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Clients;

public interface IPortalClient
{
    Task<bool> SendMetricsBatchAsync(MetricsBatchDto batch, CancellationToken ct);
    Task<bool> SendEventAsync(AgentEventDto evt, CancellationToken ct);
    Task<IReadOnlyList<AgentCommandDto>> PollCommandsAsync(CancellationToken ct);
    Task<bool> AcknowledgeCommandAsync(AcknowledgeCommandDto ack, CancellationToken ct);
    Task<bool> ReportCommandResultAsync(CommandResultDto result, CancellationToken ct);
    Task<(bool HasUpdate, RemoteAgentConfigDto? Config, string? Version)> FetchConfigurationAsync(
        string? currentVersion, CancellationToken ct);
    Task<bool> AcknowledgeConfigAsync(string version, CancellationToken ct);
}