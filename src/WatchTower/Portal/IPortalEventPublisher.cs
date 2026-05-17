using WatchTower.Models;

namespace WatchTower.Portal;

public interface IPortalEventPublisher
{
    Task PublishIncidentAsync(Incident incident, CancellationToken ct);
}
