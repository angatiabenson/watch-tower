using System.Diagnostics;
using System.Text;

using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

/// <summary>
/// Controls IIS application pools. State is read with <c>appcmd.exe</c> (fast, no module load)
/// and falls back to the WebAdministration PowerShell module; control operations use
/// PowerShell. Every child process runs async with a timeout, both output streams drained
/// concurrently, and all waits honour the caller's cancellation token.
/// </summary>
public class IisApplicationPoolManager : IIisApplicationPoolManager
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<IisApplicationPoolManager> _logger;
    private readonly string _appCmdPath;

    public IisApplicationPoolManager(ILogger<IisApplicationPoolManager> logger)
    {
        _logger = logger;
        _appCmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system32", "inetsrv", "appcmd.exe");
    }

    public async Task<ApplicationPoolStatus> GetApplicationPoolStatusAsync(
        string applicationPoolName, CancellationToken ct = default)
    {
        try
        {
            var state = await TryReadStateWithAppCmdAsync(applicationPoolName, ct)
                        ?? await ReadStateWithPowerShellAsync(applicationPoolName, ct);

            return new ApplicationPoolStatus(
                DateTime.UtcNow,
                applicationPoolName,
                state,
                string.Equals(state, "Started", StringComparison.OrdinalIgnoreCase)
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query application pool: {ApplicationPoolName}", applicationPoolName);
            return Unknown(applicationPoolName);
        }
    }

    /// <summary>
    /// <c>appcmd list apppool "&lt;name&gt;" /text:state</c>. Returns <c>null</c> (caller falls
    /// back to PowerShell) when appcmd is missing or does not give a clean answer, e.g. it exits 1
    /// with no output for a pool that does not exist.
    /// </summary>
    private async Task<string?> TryReadStateWithAppCmdAsync(string applicationPoolName, CancellationToken ct)
    {
        // A leading '/' or '-' would be parsed by appcmd as a switch rather than an identifier.
        if (!File.Exists(_appCmdPath) || applicationPoolName.StartsWith('/') || applicationPoolName.StartsWith('-'))
            return null;

        var result = await ProcessRunner.RunAsync(
            _appCmdPath, new[] { "list", "apppool", applicationPoolName, "/text:state" }, QueryTimeout, ct);

        if (!result.Succeeded)
        {
            _logger.LogDebug(
                "appcmd could not read state of {ApplicationPoolName} (exit {ExitCode}, timed out {TimedOut}): {Error}",
                applicationPoolName, result.ExitCode, result.TimedOut, FirstLine(result.StdErr) ?? FirstLine(result.StdOut));
            return null;
        }

        return FirstLine(result.StdOut);
    }

    private async Task<string> ReadStateWithPowerShellAsync(string applicationPoolName, CancellationToken ct)
    {
        var script =
            $@"$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop
$name = '{EscapePowerShellLiteral(applicationPoolName)}'
if (-not (Test-Path (""IIS:\AppPools\"" + $name))) {{
    Write-Output '{ApplicationPoolStatus.NotFoundStatus}'
    exit 0
}}
$state = (Get-WebAppPoolState -Name $name).Value
Write-Output $state";

        var result = await ExecutePowerShellAsync(script, QueryTimeout, ct);
        if (!result.Succeeded)
        {
            // A timeout or error says nothing about the pool itself: report Unknown, never down.
            _logger.LogWarning(
                "Failed to query application pool {ApplicationPoolName}: {Error}",
                applicationPoolName, ErrorText(result));
            return ApplicationPoolStatus.UnknownStatus;
        }

        return FirstLine(result.StdOut) ?? ApplicationPoolStatus.UnknownStatus;
    }

    public async Task<bool> StartApplicationPoolAsync(
        string applicationPoolName, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var currentStatus = await GetApplicationPoolStatusAsync(applicationPoolName, ct);
            if (currentStatus.IsRunning)
                return true;

            var script =
                $@"$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop
$name = '{EscapePowerShellLiteral(applicationPoolName)}'
if (-not (Test-Path (""IIS:\AppPools\"" + $name))) {{
    Write-Error ""Application pool not found: $name""
    exit 2
}}
Start-WebAppPool -Name $name -ErrorAction Stop
Write-Output 'StartRequested'";

            var result = await ExecutePowerShellAsync(script, CommandTimeout, ct);
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to start application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName, ErrorText(result));
                return false;
            }

            if (await WaitForStateAsync(applicationPoolName, running: true, timeout, ct))
            {
                _logger.LogInformation("Application pool started successfully: {ApplicationPoolName}", applicationPoolName);
                return true;
            }

            _logger.LogError("Timed out waiting for application pool to start: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start application pool: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
    }

    public async Task<bool> StopApplicationPoolAsync(
        string applicationPoolName, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var currentStatus = await GetApplicationPoolStatusAsync(applicationPoolName, ct);
            if (string.Equals(currentStatus.Status, "Stopped", StringComparison.OrdinalIgnoreCase))
                return true;

            var script =
                $@"$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop
$name = '{EscapePowerShellLiteral(applicationPoolName)}'
if (-not (Test-Path (""IIS:\AppPools\"" + $name))) {{
    Write-Error ""Application pool not found: $name""
    exit 2
}}
Stop-WebAppPool -Name $name -ErrorAction Stop
Write-Output 'StopRequested'";

            var result = await ExecutePowerShellAsync(script, CommandTimeout, ct);
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to stop application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName, ErrorText(result));
                return false;
            }

            if (await WaitForStateAsync(applicationPoolName, running: false, timeout, ct))
            {
                _logger.LogInformation("Application pool stopped successfully: {ApplicationPoolName}", applicationPoolName);
                return true;
            }

            _logger.LogError("Timed out waiting for application pool to stop: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop application pool: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
    }

    public async Task<bool> RecycleApplicationPoolAsync(
        string applicationPoolName, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var script =
                $@"$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop
$name = '{EscapePowerShellLiteral(applicationPoolName)}'
if (-not (Test-Path (""IIS:\AppPools\"" + $name))) {{
    Write-Error ""Application pool not found: $name""
    exit 2
}}
Restart-WebAppPool -Name $name -ErrorAction Stop
Write-Output 'RecycleRequested'";

            var result = await ExecutePowerShellAsync(script, timeout, ct);
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to recycle application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName, ErrorText(result));
                return false;
            }

            _logger.LogInformation("Application pool recycled successfully: {ApplicationPoolName}", applicationPoolName);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recycle application pool: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
    }

    /// <summary>Polls the pool state until it reaches the desired state or the timeout elapses.</summary>
    private async Task<bool> WaitForStateAsync(
        string applicationPoolName, bool running, TimeSpan timeout, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(StatePollInterval, ct);

            var status = await GetApplicationPoolStatusAsync(applicationPoolName, ct);
            if (running
                    ? status.IsRunning
                    : string.Equals(status.Status, "Stopped", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static ApplicationPoolStatus Unknown(string applicationPoolName) =>
        new(DateTime.UtcNow, applicationPoolName, ApplicationPoolStatus.UnknownStatus, false);

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string ErrorText(ProcessRunner.Result result) =>
        !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr.Trim() : result.StdOut.Trim();

    private static Task<ProcessRunner.Result> ExecutePowerShellAsync(
        string script, TimeSpan timeout, CancellationToken ct) =>
        ProcessRunner.RunAsync(
            "powershell.exe",
            new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            },
            timeout,
            ct);
}