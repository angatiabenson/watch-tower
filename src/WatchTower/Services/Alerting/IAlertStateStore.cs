namespace WatchTower.Services.Alerting;

/// <summary>
/// Durable alert state (<c>alert_state</c>) plus small agent facts that must survive restarts
/// (<c>agent_state</c>, e.g. the last observed boot time). Neither table is touched by the daily
/// report purge, so an alert that is open across a restart or a report is not re-sent.
/// </summary>
public interface IAlertStateStore
{
    IReadOnlyList<AlertState> LoadAll();

    void Upsert(AlertState state);

    string? GetValue(string key);

    void SetValue(string key, string value);
}