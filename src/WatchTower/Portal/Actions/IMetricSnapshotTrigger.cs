namespace WatchTower.Portal.Actions;

public interface IMetricSnapshotTrigger
{
    void Signal();
    bool TryConsume();
}

public class MetricSnapshotTrigger : IMetricSnapshotTrigger
{
    private volatile bool _pending;

    public void Signal() => _pending = true;

    public bool TryConsume()
    {
        if (!_pending) return false;
        _pending = false;
        return true;
    }
}
