using System.Reflection;

namespace WatchTower.Hosting;

/// <summary>Identity and runtime facts about this WatchTower agent instance.</summary>
public sealed class AgentInfo
{
    public AgentInfo(WatchTowerPaths paths, TimeProvider timeProvider)
    {
        Paths = paths;
        StartedAtUtc = timeProvider.GetUtcNow();
    }

    public static string ProductVersion { get; } = ResolveVersion();

    /// <summary>Semantic version without build metadata (e.g. "1.4.2").</summary>
    public static string Version { get; } = ProductVersion.Split('+')[0];

    public string MachineName { get; } = Environment.MachineName;
    public string OsDescription { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public string RuntimeVersion { get; } = Environment.Version.ToString();
    public bool IsWindowsService { get; init; } = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
    public int ProcessId { get; } = Environment.ProcessId;
    public DateTimeOffset StartedAtUtc { get; }
    public WatchTowerPaths Paths { get; }

    private static string ResolveVersion()
    {
        var assembly = typeof(AgentInfo).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "0.0.0";
    }
}