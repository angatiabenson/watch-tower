namespace WatchTower.Portal.Actions;

/// <summary>
/// Raised by the ForceMetricSnapshot portal command; wakes <c>OutboxDispatchWorker</c> so it
/// collects and pushes a metrics batch immediately instead of at the next flush interval.
/// </summary>
public interface IMetricSnapshotTrigger
{
    void Signal();
    bool TryConsume();

    /// <inheritdoc cref="AsyncTrigger.WaitAsync"/>
    Task<bool> WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken ct);
}

public class MetricSnapshotTrigger : AsyncTrigger, IMetricSnapshotTrigger
{
}