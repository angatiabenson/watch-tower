using System.ServiceProcess;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class WindowsServiceManager : IWindowsServiceManager
{
    private readonly ILogger<WindowsServiceManager> _logger;

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger)
    {
        _logger = logger;
    }

    public ServiceStatus GetServiceStatus(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            var status = sc.Status.ToString();
            var isRunning = sc.Status == ServiceControllerStatus.Running;
            return new ServiceStatus(
                DateTime.UtcNow,
                serviceName,
                sc.DisplayName,
                status,
                isRunning
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query service: {ServiceName}", serviceName);
            return new ServiceStatus(DateTime.UtcNow, serviceName, serviceName, "NotFound", false);
        }
    }

    public bool RestartService(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(serviceName);

            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("Stopping service: {ServiceName}", serviceName);
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }

            _logger.LogInformation("Starting service: {ServiceName}", serviceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);

            _logger.LogInformation("Service restarted successfully: {ServiceName}", serviceName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart service: {ServiceName}", serviceName);
            return false;
        }
    }

    public bool StopService(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(serviceName);

            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("Service {ServiceName} is already stopped", serviceName);
                return true;
            }

            _logger.LogInformation("Stopping service: {ServiceName}", serviceName);
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);

            _logger.LogInformation("Service stopped successfully: {ServiceName}", serviceName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop service: {ServiceName}", serviceName);
            return false;
        }
    }
}
