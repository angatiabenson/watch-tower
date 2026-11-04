using System.Diagnostics;

namespace WatchTower.Hosting;

/// <summary>Restarts the running WatchTower instance (used by the console, the portal and the updater).</summary>
public interface IAgentRestarter
{
    /// <summary>False when running interactively (console mode): there is no service to restart.</summary>
    bool CanRestart { get; }

    /// <summary>Schedules a restart a moment from now. Returns false (with a reason) when not possible.</summary>
    bool TryScheduleRestart(string reason, out string message);
}

/// <summary>
/// A Windows service cannot restart itself in-process, so this launches a detached helper —
/// the same executable with the <c>restart</c> command — which asks the Service Control
/// Manager to stop and then start the service. The helper outlives this process.
/// </summary>
public sealed class AgentRestarter(
    AgentInfo agent,
    InstanceIdentity instance,
    ILogger<AgentRestarter> logger) : IAgentRestarter
{
    private int _scheduled;

    public bool CanRestart => agent.IsWindowsService && Environment.ProcessPath is not null;

    public bool TryScheduleRestart(string reason, out string message)
    {
        if (!CanRestart)
        {
            message = "WatchTower is running interactively (not as a Windows service); restart it manually.";
            return false;
        }

        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            message = "A restart is already in progress.";
            return true;
        }

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = agent.Paths.DataDirectory,
        };
        foreach (var arg in new[] { "restart", "--service-name", instance.ServiceName, "--delay", "2", "--data-dir", agent.Paths.DataDirectory })
            psi.ArgumentList.Add(arg);

        try
        {
            using var _ = Process.Start(psi);
            logger.LogWarning("Restarting WatchTower: {Reason}", reason);
            message = "WatchTower is restarting; the console will be back in a few seconds.";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Interlocked.Exchange(ref _scheduled, 0);
            logger.LogError(ex, "Could not launch the restart helper");
            message = $"Could not restart: {ex.Message}";
            return false;
        }
    }
}