namespace WatchTower.Portal.Actions;

public interface IConfigSyncTrigger
{
    void Signal();
    bool TryConsume();
}

public class ConfigSyncTrigger : IConfigSyncTrigger
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