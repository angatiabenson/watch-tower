using System.Text.Json;

namespace WatchTower.Hosting;

/// <summary>Written once every hosted service has started; the update installer waits for it.</summary>
public sealed record HealthSnapshot(string Version, int ProcessId, DateTimeOffset StartedAt, string ServiceName);

/// <summary>
/// Writes <c>state/health.json</c> when the host has fully started. It is the readiness signal
/// the update installer (and deployment scripts) use to decide whether a new version came up,
/// independent of whether the web console is enabled.
/// </summary>
public sealed class HealthBeacon(
    WatchTowerPaths paths,
    InstanceIdentity instance,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<HealthBeacon> logger) : IHostedService
{
    public static string PathFor(WatchTowerPaths paths) => Path.Combine(paths.StateDirectory, "health.json");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(Write);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Write()
    {
        try
        {
            var snapshot = new HealthSnapshot(AgentInfo.Version, Environment.ProcessId, time.GetUtcNow(), instance.ServiceName);
            Directory.CreateDirectory(paths.StateDirectory);
            var path = PathFor(paths);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the health beacon");
        }
    }

    public static HealthSnapshot? Read(WatchTowerPaths paths)
    {
        try
        {
            var path = PathFor(paths);
            return File.Exists(path) ? JsonSerializer.Deserialize<HealthSnapshot>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}