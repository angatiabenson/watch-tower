namespace WatchTower.Services.Alerting;

/// <summary>
/// Durable alert state (<c>alert_state</c>), notifications still being delivered
/// (<c>alert_delivery</c>) plus small agent facts that must survive restarts (<c>agent_state</c>,
/// e.g. the last observed boot time). None of these tables is touched by the daily report purge,
/// so an alert that is open across a restart or a report is not re-sent, and a pending delivery
/// resumes after a restart.
/// </summary>
public interface IAlertStateStore
{
    IReadOnlyList<AlertState> LoadAll();

    void Upsert(AlertState state);

    IReadOnlyList<PendingDelivery> LoadDeliveries();

    void UpsertDelivery(PendingDelivery delivery);

    void DeleteDelivery(string id);

    string? GetValue(string key);

    void SetValue(string key, string value);
}