using WatchTower.Models;

namespace WatchTower.Portal;

public class NullPortalEventPublisher : IPortalEventPublisher
{
    public Task PublishIncidentAsync(Incident incident, CancellationToken ct)
        => Task.CompletedTask;

    public Task PublishAlertEventAsync(Incident incident, string alertKey, bool? resolved, CancellationToken ct)
        => Task.CompletedTask;
}