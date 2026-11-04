using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.Text.Json;

using WatchTower.Hosting;

namespace WatchTower.Updates;

/// <summary>
/// The installer half of the self-update. Runs as a separate process from the staged copy of
/// the NEW WatchTower.exe (a running exe cannot overwrite itself):
/// stop service → wait for the old process → back up the install folder → copy the new files →
/// start service → wait for the new version's health beacon → roll back on any failure.
/// Every step is logged to <c>Logs/update-*.log</c>; the outcome goes to
/// <c>updates/last-result.json</c> and is shown in the console after the restart.
/// </summary>
public sealed class UpdateApplier
{
    public sealed record Request(
        string ServiceName,
        WatchTowerPaths Paths,
        string SourceDirectory,
        string TargetDirectory,
        int OldProcessId,
        string FromVersion,
        string ToVersion,
        TimeSpan HealthTimeout);

    private readonly Request _request;
    private readonly IServiceControl _services;
    private readonly TextWriter _log;
    private readonly Func<TimeSpan, Task> _delay;

    public UpdateApplier(Request request, IServiceControl services, TextWriter log, Func<TimeSpan, Task>? delay = null)
    {
        _request = request;
        _services = services;
        _log = log;
        _delay = delay ?? (t => Task.Delay(t));
    }

    public string BackupDirectory => Path.Combine(_request.Paths.UpdatesDirectory, "backup", _request.FromVersion);

    public async Task<UpdateResult> RunAsync()
    {
        var r = _request;

        // Only one installer may ever touch the service and install folder at a time.
        Directory.CreateDirectory(r.Paths.UpdatesDirectory);
        FileStream installLock;
        try
        {
            installLock = new FileStream(Path.Combine(r.Paths.UpdatesDirectory, "install.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            Log($"Another installation is already running; not installing {r.ToVersion} again.");
            // Do not write last-result.json: the running installer owns the outcome.
            return new UpdateResult(r.FromVersion, r.ToVersion, false, "Another installation is already running.",
                DateTimeOffset.UtcNow);
        }

        using (installLock)
            return await RunLockedAsync();
    }

    private async Task<UpdateResult> RunLockedAsync()
    {
        var r = _request;
        Log($"Updating WatchTower {r.FromVersion} -> {r.ToVersion} (service '{r.ServiceName}', target '{r.TargetDirectory}')");

        if (string.Equals(Path.GetFullPath(r.SourceDirectory).TrimEnd('\\'), Path.GetFullPath(r.TargetDirectory).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            return Finish(false, "The update source and target folders are the same.");

        var backedUp = false;
        try
        {
            // Give the console a moment to deliver its "installing…" response.
            await _delay(TimeSpan.FromSeconds(2));

            await StopServiceAsync();
            await WaitForExitAsync(r.OldProcessId, TimeSpan.FromSeconds(60));

            BackUp();
            backedUp = true;

            InstallPackage(r.SourceDirectory, r.TargetDirectory, retries: 10);
            Log("New files copied");

            var beacon = HealthBeacon.PathFor(r.Paths);
            if (File.Exists(beacon))
                Try(() => File.Delete(beacon)); // only the NEW process may vouch for itself
            await StartServiceAsync();

            if (await WaitForHealthAsync())
            {
                PruneBackups(keep: 2);
                return Finish(true, $"Updated from {r.FromVersion} to {r.ToVersion}.");
            }

            Log("The new version did not report healthy in time; rolling back");
            return await RollBackAsync()
                ? Finish(false, $"Version {r.ToVersion} did not start correctly, so {r.FromVersion} was restored. " +
                                "See Logs/update-*.log for details.")
                : Finish(false, $"Version {r.ToVersion} did not start correctly. {r.FromVersion} was restored but " +
                                "has not reported healthy yet; check the service and Logs/update-*.log.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            Log($"Update failed: {ex}");
            if (backedUp)
            {
                try
                {
                    if (!await RollBackAsync())
                        return Finish(false, $"The update to {r.ToVersion} failed ({ex.Message}). {r.FromVersion} was " +
                                             "restored but has not reported healthy yet; check the service.");
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException
                                                          or InvalidOperationException or System.ServiceProcess.TimeoutException)
                {
                    Log($"ROLLBACK FAILED: {rollbackError}");
                    return Finish(false, $"The update failed and the automatic rollback also failed: {rollbackError.Message}. " +
                                         $"Restore '{BackupDirectory}' into '{r.TargetDirectory}' manually.");
                }
            }
            else
            {
                // Nothing was changed; just make sure the old version is running again.
                await TryStartAsync();
            }
            return Finish(false, $"The update to {r.ToVersion} failed and {r.FromVersion} is still installed: {ex.Message}");
        }
    }

    private async Task StopServiceAsync()
    {
        Log("Stopping the service");
        await _services.StopAsync(_request.ServiceName, TimeSpan.FromSeconds(90));
    }

    private async Task StartServiceAsync()
    {
        Log("Starting the service");
        await _services.StartAsync(_request.ServiceName, TimeSpan.FromSeconds(90));
    }

    private async Task TryStartAsync()
    {
        try { await StartServiceAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Log($"Could not restart the service: {ex.Message}");
        }
    }

    private async Task WaitForExitAsync(int pid, TimeSpan timeout)
    {
        Process? process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // already gone

        using (process)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log($"Process {pid} did not exit in time; terminating it");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private void BackUp()
    {
        if (Directory.Exists(BackupDirectory))
            Directory.Delete(BackupDirectory, recursive: true);
        CopyDirectory(_request.TargetDirectory, BackupDirectory, retries: 3);
        Log($"Backed up {_request.FromVersion} to {BackupDirectory}");
    }

    /// <summary>Restores the backup and returns whether the restored version reported healthy.</summary>
    private async Task<bool> RollBackAsync()
    {
        Log("Rolling back");
        try { await StopServiceAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Log($"Stopping the failed version: {ex.Message}");
        }
        // Restore the exact previous file set: files the failed release ADDED (e.g. a new
        // appsettings.Production.json) would otherwise survive and break the restored version.
        MirrorDirectory(BackupDirectory, _request.TargetDirectory, retries: 10);

        var beacon = HealthBeacon.PathFor(_request.Paths);
        if (File.Exists(beacon))
            Try(() => File.Delete(beacon));
        await StartServiceAsync();

        var healthy = await WaitForHealthAsync(_request.FromVersion);
        Log(healthy ? "Rollback complete; the previous version is healthy" : "Rollback done, but the previous version has not reported healthy");
        return healthy;
    }

    private const string PackageListFile = ".watchtower-files";

    /// <summary>
    /// Copies the new package over the install folder, then deletes files that belonged to the
    /// PREVIOUS package but not to the new one (tracked in <c>.watchtower-files</c>). Files the
    /// package never shipped — data from very old installs, operator files — are never touched.
    /// </summary>
    internal static void InstallPackage(string source, string target, int retries)
    {
        var listPath = Path.Combine(target, PackageListFile);
        var previous = File.Exists(listPath) ? File.ReadAllLines(listPath) : [];

        CopyDirectory(source, target, retries);

        var current = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in previous.Where(p => p.Length > 0 && !current.Contains(p)))
        {
            var full = Path.GetFullPath(Path.Combine(target, relative));
            // Never follow a crafted list outside the install folder.
            if (full.StartsWith(Path.GetFullPath(target).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(full))
                File.Delete(full);
        }

        File.WriteAllLines(listPath, current.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Makes <paramref name="target"/> identical to <paramref name="source"/> (copy + delete extras).</summary>
    internal static void MirrorDirectory(string source, string target, int retries)
    {
        CopyDirectory(source, target, retries);

        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            if (!File.Exists(Path.Combine(source, Path.GetRelativePath(target, file))))
                File.Delete(file);
        }
        foreach (var dir in Directory.GetDirectories(target, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.Exists(Path.Combine(source, Path.GetRelativePath(target, dir))) &&
                !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    private Task<bool> WaitForHealthAsync() => WaitForHealthAsync(_request.ToVersion);

    private async Task<bool> WaitForHealthAsync(string version)
    {
        var deadline = DateTime.UtcNow + _request.HealthTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await _delay(TimeSpan.FromSeconds(2));
            var health = HealthBeacon.Read(_request.Paths);
            if (health is null || !string.Equals(health.Version, version, StringComparison.OrdinalIgnoreCase))
                continue;

            // Healthy once started — and still running a little later (no crash loop).
            await _delay(TimeSpan.FromSeconds(10));
            if (_services.IsRunning(_request.ServiceName) && IsAlive(health.ProcessId))
            {
                Log($"Version {health.Version} is running (pid {health.ProcessId})");
                return true;
            }
            Log($"Version {version} started but did not stay up");
            return false;
        }
        return false;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void PruneBackups(int keep)
    {
        var root = Path.Combine(_request.Paths.UpdatesDirectory, "backup");
        if (!Directory.Exists(root)) return;
        foreach (var old in new DirectoryInfo(root).GetDirectories()
                     .OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
            Try(() => old.Delete(recursive: true));
    }

    internal static void CopyDirectory(string source, string target, int retries)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, destination, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < retries)
                {
                    Thread.Sleep(500); // a just-stopped process may still hold the file briefly
                }
            }
        }
    }

    private UpdateResult Finish(bool success, string message)
    {
        var result = new UpdateResult(_request.FromVersion, _request.ToVersion, success, message, DateTimeOffset.UtcNow);
        Log((success ? "SUCCESS: " : "FAILED: ") + message);
        Try(() =>
        {
            Directory.CreateDirectory(_request.Paths.UpdatesDirectory);
            var path = Path.Combine(_request.Paths.UpdatesDirectory, "last-result.json");
            File.WriteAllText(path, JsonSerializer.Serialize(result, UpdateFeedClient.Json));
        });
        return result;
    }

    private void Log(string message)
    {
        _log.WriteLine($"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} {message}");
        _log.Flush();
    }

    private static bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Service control seam (real SCM in production, fakes in tests).</summary>
public interface IServiceControl
{
    Task StopAsync(string serviceName, TimeSpan timeout);
    Task StartAsync(string serviceName, TimeSpan timeout);
    bool IsRunning(string serviceName);
}

public sealed class WindowsServiceControl : IServiceControl
{
    public Task StopAsync(string serviceName, TimeSpan timeout) => Task.Run(() =>
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    });

    public Task StartAsync(string serviceName, TimeSpan timeout) => Task.Run(() =>
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    });

    public bool IsRunning(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        return sc.Status == ServiceControllerStatus.Running;
    }
}