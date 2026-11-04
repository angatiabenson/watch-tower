using WatchTower.Models;

namespace WatchTower.Services.Abstractions;

public interface IIisApplicationPoolManager
{
    /// <summary>
    /// Reads the pool state. Never throws for query failures: a timeout or error yields
    /// <see cref="ApplicationPoolStatus.UnknownStatus"/>, a missing pool
    /// <see cref="ApplicationPoolStatus.NotFoundStatus"/>.
    /// </summary>
    Task<ApplicationPoolStatus> GetApplicationPoolStatusAsync(string applicationPoolName, CancellationToken ct = default);

    Task<bool> StartApplicationPoolAsync(string applicationPoolName, TimeSpan timeout, CancellationToken ct = default);
    Task<bool> StopApplicationPoolAsync(string applicationPoolName, TimeSpan timeout, CancellationToken ct = default);
    Task<bool> RecycleApplicationPoolAsync(string applicationPoolName, TimeSpan timeout, CancellationToken ct = default);
}