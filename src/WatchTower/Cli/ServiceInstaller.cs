using System.Diagnostics;
using System.ServiceProcess;

namespace WatchTower.Cli;

/// <summary>Registers / controls the WatchTower Windows service via the Service Control Manager.</summary>
internal sealed class ServiceInstaller(string serviceName, TextWriter output)
{
    public string ServiceName { get; } = serviceName;

    public bool IsInstalled()
    {
        return ServiceController.GetServices()
            .Any(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
    }

    public ServiceControllerStatus? GetStatus()
    {
        if (!IsInstalled()) return null;
        using var sc = new ServiceController(ServiceName);
        return sc.Status;
    }

    /// <summary>Creates the service, or re-points an existing registration at <paramref name="binaryPath"/>.</summary>
    public void InstallOrUpdate(string executablePath, IReadOnlyList<string> serviceArguments, string displayName, string description)
    {
        var binaryPath = Quote(executablePath) +
                         string.Concat(serviceArguments.Select(a => " " + (a.Contains(' ') ? Quote(a) : a)));

        if (IsInstalled())
        {
            output.WriteLine($"Service '{ServiceName}' already exists; updating its configuration.");
            Sc("config", ServiceName, "binPath=", binaryPath, "start=", "delayed-auto", "DisplayName=", displayName);
        }
        else
        {
            Sc("create", ServiceName, "binPath=", binaryPath, "start=", "delayed-auto", "DisplayName=", displayName);
        }

        Sc("description", ServiceName, description);

        // Crash recovery: restart after 1 min, 1 min, then 5 min; reset the failure count daily.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        // Also apply recovery when the service stops with a non-zero exit code (e.g. fatal config error).
        Sc("failureflag", ServiceName, "1");
    }

    public void Uninstall()
    {
        if (!IsInstalled())
        {
            output.WriteLine($"Service '{ServiceName}' is not installed.");
            return;
        }
        Stop(TimeSpan.FromSeconds(60));
        Sc("delete", ServiceName);
    }

    public void Start(TimeSpan timeout)
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending)
            sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public void Stop(TimeSpan timeout)
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending && sc.CanStop)
            sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    private void Sc(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sc.exe");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"sc.exe {args[0]} failed with exit code {process.ExitCode}: {(stdout + stderr).Trim()}");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}