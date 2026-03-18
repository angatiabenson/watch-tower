using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface IWindowsServiceManager
{
    ServiceStatus GetServiceStatus(string serviceName);
    bool RestartService(string serviceName, TimeSpan timeout);
}
