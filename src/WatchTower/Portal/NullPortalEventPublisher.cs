using WatchTower.Models;

namespace WatchTower.Portal;

public class NullPortalEventPublisher : IPortalEventPublisher
{
    public Task PublishIncidentAsync(Incident incident, CancellationToken ct)
        => Task.CompletedTask;
}