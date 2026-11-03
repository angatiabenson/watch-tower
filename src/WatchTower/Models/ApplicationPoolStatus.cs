namespace WatchTower.Models;

public record ApplicationPoolStatus(
    DateTime Timestamp,
    string ApplicationPoolName,
    string Status,
    bool IsRunning
)
{
    /// <summary>
    /// Status reported when the pool's state could not be determined (query timed out or
    /// failed). It is neither running nor down: monitors must not raise incidents or start the
    /// pool on the strength of it.
    /// </summary>
    public const string UnknownStatus = "Unknown";

    /// <summary>Status reported when IIS has no application pool with that name.</summary>
    public const string NotFoundStatus = "NotFound";

    /// <summary>True when the state could not be determined (see <see cref="UnknownStatus"/>).</summary>
    public static bool IsUnknown(string status) =>
        string.Equals(status, UnknownStatus, StringComparison.OrdinalIgnoreCase);
}