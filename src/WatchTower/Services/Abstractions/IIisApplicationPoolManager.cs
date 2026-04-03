using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface IIisApplicationPoolManager
{
    ApplicationPoolStatus GetApplicationPoolStatus(string applicationPoolName);
    bool StartApplicationPool(string applicationPoolName, TimeSpan timeout);
}
