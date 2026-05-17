using System.Text.Json;
using WatchTower.Models;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;

namespace WatchTower.Portal;

public class PortalEventPublisher : IPortalEventPublisher
{
    private readonly IOutboxStore _outbox;
    private readonly ILogger<PortalEventPublisher> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public PortalEventPublisher(IOutboxStore outbox, ILogger<PortalEventPublisher> logger)
    {
        _outbox = outbox;
        _logger = logger;
    }

    public async Task PublishIncidentAsync(Incident incident, CancellationToken ct)
    {
        try
        {
            var dto = new AgentEventDto(
                Guid.NewGuid().ToString(),
                incident.Timestamp.ToString("O"),
                (int)incident.Type,
                incident.Description,
                incident.TopProcesses?.Select(p =>
                    new ProcessInfoDto(p.Name, p.Pid, p.CpuSeconds, p.MemoryMb, p.CpuPercent)
                ).ToList()
            );

            var json = JsonSerializer.Serialize(dto, JsonOpts);
            await _outbox.AddMessageAsync("AgentEvent", json, priority: 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue portal event for incident {Type}", incident.Type);
        }
    }
}
