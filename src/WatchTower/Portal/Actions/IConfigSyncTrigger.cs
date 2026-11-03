namespace WatchTower.Portal.Actions;

/// <summary>
/// Raised by the ForceConfigSync portal command; wakes <c>RemoteConfigWorker</c> so it fetches
/// configuration immediately instead of after its full poll interval.
/// </summary>
public interface IConfigSyncTrigger
{
    void Signal();
    bool TryConsume();

    /// <inheritdoc cref="AsyncTrigger.WaitAsync"/>
    Task<bool> WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken ct);
}

public class ConfigSyncTrigger : AsyncTrigger, IConfigSyncTrigger
{
}