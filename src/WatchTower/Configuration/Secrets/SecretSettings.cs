namespace WatchTower.Configuration.Secrets;

/// <summary>
/// Configuration keys whose values are secrets: encrypted when written to the operator
/// settings file and never returned in clear text by the configuration UI/API.
/// </summary>
public static class SecretSettings
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Email:Password",
        "VisibilityPortal:AgentToken",
        // The healthchecks.io ping URL embeds the check UUID; anyone holding it can forge pings.
        "Heartbeat:Url",
    };

    /// <summary>Key prefixes whose every descendant is secret (e.g. per-channel webhook URLs).</summary>
    private static readonly string[] Prefixes = [];

    public static IReadOnlyCollection<string> All => Keys;

    public static bool IsSecret(string configurationKey) =>
        Keys.Contains(configurationKey) ||
        Prefixes.Any(p => configurationKey.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers an additional secret key (used by features that add their own secrets).</summary>
    public static void Register(string configurationKey) => Keys.Add(configurationKey);
}