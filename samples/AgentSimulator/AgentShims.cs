// The agent's RemoteConfigValidator (linked from src/WatchTower) needs two maintenance helpers
// that live in the agent's alerting code. Rather than compiling that whole area here, these are
// copies of the two members it uses, from src/WatchTower/Services/Alerting/MaintenanceState.cs.
// The contract tests check the agent's values against the spec (7 days, 500 characters).

using System.Globalization;

namespace WatchTower.Services.Alerting;

public static class MaintenanceState
{
    /// <summary>Parses an ISO-8601 timestamp; values without an offset are taken as UTC.</summary>
    public static bool TryParseUntil(string? value, out DateTimeOffset until)
    {
        until = default;
        return !string.IsNullOrWhiteSpace(value) &&
               DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out until);
    }

    public static string Format(DateTimeOffset until) => until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

public static class MaintenanceWindowRules
{
    public const int MaxMinutes = 7 * 24 * 60;

    public const int MaxReasonLength = 500;
}