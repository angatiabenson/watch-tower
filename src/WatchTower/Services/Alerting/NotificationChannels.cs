using WatchTower.Configuration;

namespace WatchTower.Services.Alerting;

public enum NotificationChannelType
{
    Email,
    Webhook,
    Teams,
    Slack,
}

/// <summary>An enabled channel after merging the fixed settings and the <c>Channels</c> list.</summary>
/// <param name="Url">Delivery URL (secret: never log it). Null for email.</param>
/// <param name="Secret">HMAC signing secret (secret). Null when unsigned.</param>
public sealed record NotificationChannel(
    string Name,
    NotificationChannelType Type,
    AlertSeverity MinSeverity,
    string? Url = null,
    string? Secret = null)
{
    /// <summary>Never includes the URL or secret.</summary>
    public override string ToString() => $"{Name} ({Type})";
}

/// <summary>Turns <see cref="NotificationOptions"/> into the list of enabled channels.</summary>
public static class NotificationChannelResolver
{
    public static IReadOnlyList<NotificationChannel> Resolve(NotificationOptions options)
    {
        var channels = new List<NotificationChannel>();

        if (options.Email.Enabled)
            channels.Add(new NotificationChannel("email", NotificationChannelType.Email, ParseSeverity(options.Email.MinSeverity)));
        AddWebhook("teams", NotificationChannelType.Teams, options.Teams);
        AddWebhook("slack", NotificationChannelType.Slack, options.Slack);
        AddWebhook("webhook", NotificationChannelType.Webhook, options.Webhook);

        for (var i = 0; i < options.Channels.Count; i++)
        {
            var entry = options.Channels[i];
            if (!TryParseType(entry.Type, out var type))
                continue; // reported by StartupValidator
            var name = string.IsNullOrWhiteSpace(entry.Name)
                ? $"{type.ToString().ToLowerInvariant()}-{i}"
                : entry.Name.Trim();

            // A list entry replaces a fixed channel (or an earlier entry) with the same name.
            channels.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!entry.Enabled)
                continue;
            if (type != NotificationChannelType.Email && string.IsNullOrWhiteSpace(entry.Url))
                continue;

            channels.Add(new NotificationChannel(name, type, ParseSeverity(entry.MinSeverity),
                type == NotificationChannelType.Email ? null : entry.Url.Trim(),
                string.IsNullOrEmpty(entry.Secret) ? null : entry.Secret));
        }

        return channels;

        void AddWebhook(string name, NotificationChannelType type, WebhookNotificationSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.Url))
                return;
            channels.Add(new NotificationChannel(name, type, ParseSeverity(settings.MinSeverity), settings.Url.Trim(),
                type == NotificationChannelType.Webhook && !string.IsNullOrEmpty(settings.Secret) ? settings.Secret : null));
        }
    }

    public static bool TryParseType(string? value, out NotificationChannelType type) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type);

    public static bool TryParseSeverity(string? value, out AlertSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            severity = AlertSeverity.Info;
            return true;
        }
        return Enum.TryParse(value.Trim(), ignoreCase: true, out severity) && Enum.IsDefined(severity)
               && !int.TryParse(value, out _);
    }

    private static AlertSeverity ParseSeverity(string? value) =>
        TryParseSeverity(value, out var severity) ? severity : AlertSeverity.Info;

    /// <summary>True for an absolute http(s) URL.</summary>
    public static bool IsValidUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}