using System.Diagnostics;
using System.Text;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class IisApplicationPoolManager : IIisApplicationPoolManager
{
    private readonly ILogger<IisApplicationPoolManager> _logger;

    public IisApplicationPoolManager(ILogger<IisApplicationPoolManager> logger)
    {
        _logger = logger;
    }

    public ApplicationPoolStatus GetApplicationPoolStatus(string applicationPoolName)
    {
        try
        {
            var script =
                $@"$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop
$name = '{EscapePowerShellLiteral(applicationPoolName)}'
if (-not (Test-Path (""IIS:\AppPools\"" + $name))) {{
    Write-Output 'NotFound'
    exit 0
}}
$state = (Get-WebAppPoolState -Name $name).Value
Write-Output $state";

            var result = ExecutePowerShell(script, TimeSpan.FromSeconds(10));
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to query application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName,
                    result.Error
                );
                return new ApplicationPoolStatus(
                    DateTime.UtcNow,
                    applicationPoolName,
                    "Unavailable",
                    false
                );
            }

            var status = NormalizeStatus(result.Output);
            return new ApplicationPoolStatus(
                DateTime.UtcNow,
                applicationPoolName,
                status,
                string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to query application pool: {ApplicationPoolName}",
                applicationPoolName
            );
            return new ApplicationPoolStatus(
                DateTime.UtcNow,
                applicationPoolName,
                "Unavailable",
                false
            );
        }
    }

    public bool StartApplicationPool(string applicationPoolName, TimeSpan timeout)
    {
        try
        {
            var currentStatus = GetApplicationPoolStatus(applicationPoolName);
            if (currentStatus.IsRunning)
            {
                return true;
            }

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

            var result = ExecutePowerShell(script, TimeSpan.FromSeconds(10));
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to start application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName,
                    result.Error
                );
                return false;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));

                var status = GetApplicationPoolStatus(applicationPoolName);
                if (status.IsRunning)
                {
                    _logger.LogInformation(
                        "Application pool started successfully: {ApplicationPoolName}",
                        applicationPoolName
                    );
                    return true;
                }
            }

            _logger.LogError(
                "Timed out waiting for application pool to start: {ApplicationPoolName}",
                applicationPoolName
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start application pool: {ApplicationPoolName}",
                applicationPoolName
            );
            return false;
        }
    }

    public bool StopApplicationPool(string applicationPoolName, TimeSpan timeout)
    {
        try
        {
            var currentStatus = GetApplicationPoolStatus(applicationPoolName);
            if (!currentStatus.IsRunning)
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

            var result = ExecutePowerShell(script, TimeSpan.FromSeconds(10));
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to stop application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName, result.Error);
                return false;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                var status = GetApplicationPoolStatus(applicationPoolName);
                if (!status.IsRunning)
                {
                    _logger.LogInformation(
                        "Application pool stopped successfully: {ApplicationPoolName}",
                        applicationPoolName);
                    return true;
                }
            }

            _logger.LogError(
                "Timed out waiting for application pool to stop: {ApplicationPoolName}",
                applicationPoolName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop application pool: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
    }

    public bool RecycleApplicationPool(string applicationPoolName, TimeSpan timeout)
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

            var result = ExecutePowerShell(script, timeout);
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Failed to recycle application pool {ApplicationPoolName}: {Error}",
                    applicationPoolName, result.Error);
                return false;
            }

            _logger.LogInformation(
                "Application pool recycled successfully: {ApplicationPoolName}", applicationPoolName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recycle application pool: {ApplicationPoolName}", applicationPoolName);
            return false;
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static string NormalizeStatus(string output)
    {
        var status = output.Trim();
        return string.IsNullOrWhiteSpace(status) ? "Unknown" : status;
    }

    private static PowerShellResult ExecutePowerShell(string script, TimeSpan timeout)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {EncodeScript(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }

            return new PowerShellResult(false, string.Empty, "Timed out");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        return new PowerShellResult(
            process.ExitCode == 0,
            output,
            string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim()
        );
    }

    private static string EncodeScript(string script)
    {
        var bytes = Encoding.Unicode.GetBytes(script);
        return Convert.ToBase64String(bytes);
    }

    private sealed record PowerShellResult(bool Succeeded, string Output, string Error);
}
