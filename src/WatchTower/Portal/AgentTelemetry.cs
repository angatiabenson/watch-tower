using WatchTower.Hosting;
using WatchTower.Portal.Contracts;
using WatchTower.Services.Alerting;
using WatchTower.Updates;

namespace WatchTower.Portal;

/// <summary>Builds the agent self-description attached to every metrics batch.</summary>
public interface IAgentTelemetry
{
    AgentTelemetryDto Snapshot();
}

public sealed class AgentTelemetry(AgentInfo agent, UpdateService updates, TimeProvider time, IMaintenanceState? maintenance = null)
    : IAgentTelemetry
{
    public AgentTelemetryDto Snapshot()
    {
        var status = updates.GetStatus();
        var window = (maintenance ?? NoMaintenance.Instance).Current;
        return new AgentTelemetryDto(
            AgentInfo.Version,
            agent.MachineName,
            agent.OsDescription,
            agent.StartedAtUtc.ToString("O"),
            (long)(time.GetUtcNow() - agent.StartedAtUtc).TotalSeconds,
            status.Phase.ToString(),
            status.Available?.Version,
            status.LastResult is { } r
                ? new AgentUpdateResultDto(r.ToVersion, r.Success, r.CompletedAt.ToString("O"))
                : null,
            new MaintenanceTelemetryDto(
                window.Active,
                window.Until is { } until ? MaintenanceState.Format(until) : null,
                window.Reason));
    }
}