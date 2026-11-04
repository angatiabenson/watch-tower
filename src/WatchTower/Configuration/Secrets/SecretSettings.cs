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
        // Incoming-webhook URLs are bearer credentials: anyone holding one can post as WatchTower.
        "Notifications:Teams:Url",
        "Notifications:Slack:Url",
        "Notifications:Webhook:Url",
        "Notifications:Webhook:Secret",
    };

    /// <summary>
    /// Key patterns where <c>*</c> matches exactly one path segment, for secrets inside lists
    /// (e.g. <c>Notifications:Channels:0:Url</c>).
    /// </summary>
    private static readonly string[][] Patterns =
    [
        ["Notifications", "Channels", "*", "Url"],
        ["Notifications", "Channels", "*", "Secret"],
    ];

    public static IReadOnlyCollection<string> All => Keys;

    public static bool IsSecret(string configurationKey) =>
        Keys.Contains(configurationKey) || Patterns.Any(p => Matches(p, configurationKey));

    /// <summary>Registers an additional secret key (used by features that add their own secrets).</summary>
    public static void Register(string configurationKey) => Keys.Add(configurationKey);

    private static bool Matches(string[] pattern, string key)
    {
        var segments = key.Split(':');
        if (segments.Length != pattern.Length)
            return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != "*" && !string.Equals(pattern[i], segments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}