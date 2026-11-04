using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace WatchTower.Ui;

/// <summary>Lookups that feed the console's pickers (services, app pools, time zones).</summary>
public sealed partial class SystemInventory(ILogger<SystemInventory> logger)
{
    private static readonly string AppCmdPath =
        Path.Combine(Environment.SystemDirectory, "inetsrv", "appcmd.exe");

    public IReadOnlyList<object> ListServices()
    {
        var services = ServiceController.GetServices();
        try
        {
            return services
                .Select(s =>
                {
                    string status, startType;
                    try { status = s.Status.ToString(); } catch (InvalidOperationException) { status = "Unknown"; }
                    try { startType = s.StartType.ToString(); } catch (InvalidOperationException) { startType = "Unknown"; }
                    return new { name = s.ServiceName, displayName = s.DisplayName, status, startType };
                })
                .OrderBy(s => s.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList<object>();
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }

    public async Task<object> ListAppPoolsAsync(CancellationToken ct)
    {
        if (!File.Exists(AppCmdPath))
            return new { iisInstalled = false, pools = Array.Empty<object>() };

        var psi = new ProcessStartInfo(AppCmdPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("apppool");

        try
        {
            using var process = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            // Read both streams concurrently with the wait so a full pipe can never deadlock.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;

            var pools = AppPoolLineRegex().Matches(output)
                .Select(m => new { name = m.Groups["name"].Value, state = m.Groups["state"].Success ? m.Groups["state"].Value : "Unknown" })
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList<object>();
            return new { iisInstalled = true, pools };
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not list IIS application pools");
            return new { iisInstalled = true, pools = Array.Empty<object>() };
        }
    }

    public static IReadOnlyList<object> ListTimeZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(z => new
            {
                // Prefer IANA ids: portable and what the shipped defaults use.
                id = TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id,
                displayName = z.DisplayName,
            })
            .DistinctBy(z => z.id, StringComparer.OrdinalIgnoreCase)
            .ToList<object>();

    // APPPOOL "DefaultAppPool" (MgdVersion:v4.0,MgdMode:Integrated,state:Started)
    [GeneratedRegex("""^APPPOOL\s+"(?<name>[^"]+)"(?:.*?state:(?<state>\w+))?""", RegexOptions.Multiline)]
    private static partial Regex AppPoolLineRegex();
}