using WatchTower.Models;

namespace WatchTower.Portal;

public interface IPortalEventPublisher
{
    Task PublishIncidentAsync(Incident incident, CancellationToken ct);

    /// <summary>
    /// Publishes an incident that belongs to an alert lifecycle: <paramref name="alertKey"/> and
    /// <paramref name="resolved"/> (null for one-shot events) travel as <c>alertKey</c>/<c>resolved</c>.
    /// </summary>
    Task PublishAlertEventAsync(Incident incident, string alertKey, bool? resolved, CancellationToken ct);
}