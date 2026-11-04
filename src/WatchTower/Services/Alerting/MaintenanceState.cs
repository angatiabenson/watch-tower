using System.Globalization;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;

namespace WatchTower.Services.Alerting;

/// <summary>The maintenance window in effect (from operator settings or the portal overlay).</summary>
/// <param name="Until">End of the window (UTC); null when none is configured or it is unreadable.</param>
public sealed record MaintenanceWindow(bool Active, DateTimeOffset? Until, string? Reason);

/// <summary>
/// Whether a maintenance window is active. During maintenance incidents are still recorded and
/// published, but notifications are held back and services / application pools are not
/// restarted automatically (administrators are working on the server).
/// </summary>
public interface IMaintenanceState
{
    MaintenanceWindow Current { get; }
}

public sealed class MaintenanceState(IOptionsMonitor<MaintenanceOptions> options, TimeProvider time) : IMaintenanceState
{
    public MaintenanceWindow Current
    {
        get
        {
            var o = options.CurrentValue;
            var reason = string.IsNullOrWhiteSpace(o.Reason) ? null : o.Reason.Trim();
            if (!TryParseUntil(o.Until, out var until))
                return new MaintenanceWindow(false, null, reason);
            return new MaintenanceWindow(until > time.GetUtcNow(), until, reason);
        }
    }

    /// <summary>Parses an ISO-8601 timestamp; values without an offset are taken as UTC.</summary>
    public static bool TryParseUntil(string? value, out DateTimeOffset until)
    {
        until = default;
        return !string.IsNullOrWhiteSpace(value) &&
               DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out until);
    }

    /// <summary>Canonical stored form: round-trip UTC, e.g. 2026-11-03T18:00:00.0000000Z.</summary>
    public static string Format(DateTimeOffset until) => until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Never in maintenance (tests, tools).</summary>
public sealed class NoMaintenance : IMaintenanceState
{
    public static readonly NoMaintenance Instance = new();
    public MaintenanceWindow Current { get; } = new(false, null, null);
}

/// <summary>Limits shared by the CLI, the console and portal-pushed maintenance windows.</summary>
public static class MaintenanceWindowRules
{
    /// <summary>Seven days.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    public const int MaxReasonLength = 500;
}