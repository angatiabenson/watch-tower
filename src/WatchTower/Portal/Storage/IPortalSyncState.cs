namespace WatchTower.Portal.Storage;

public interface IPortalSyncState
{
    Task<long> GetLastForwardedRowIdAsync(string metricType);
    Task SetLastForwardedRowIdAsync(string metricType, long rowId);
}