using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using WatchTower.Hosting;

namespace WatchTower.Services.Alerting;

/// <summary>
/// Base for channels that POST a JSON document to a webhook URL. Redirects are not followed
/// (the payload must not be forwarded to another host) and the HTTP client has logging removed,
/// so the URL — which is the credential for these services — never reaches the logs.
/// </summary>
public abstract class HttpNotificationChannel(IHttpClientFactory httpClientFactory) : INotificationChannel
{
    public const string HttpClientName = "Notifications";

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public abstract NotificationChannelType Type { get; }

    public bool IsAvailable(NotificationChannel channel) => NotificationChannelResolver.IsValidUrl(channel.Url);

    /// <summary>The request body for <paramref name="notification"/>.</summary>
    public abstract string BuildPayload(AlertNotification notification);

    protected virtual void AddHeaders(HttpRequestMessage request, NotificationChannel channel, AlertNotification notification, byte[] body)
    {
    }

    public async Task<ChannelSendResult> SendAsync(NotificationChannel channel, AlertNotification notification, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(BuildPayload(notification));
        using var request = new HttpRequestMessage(HttpMethod.Post, channel.Url!.Trim());
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WatchTower", AgentInfo.Version));
        AddHeaders(request, channel, notification, body);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? ChannelSendResult.Ok($"Delivered (HTTP {status})")
                : ChannelSendResult.Failed($"Rejected with HTTP {status} {response.ReasonPhrase}".TrimEnd());
        }
        catch (HttpRequestException ex)
        {
            // The exception message can name the host; report the error category only.
            return ChannelSendResult.Failed(ex.HttpRequestError == HttpRequestError.Unknown
                ? "Request failed"
                : $"Request failed ({ex.HttpRequestError})");
        }
    }

    protected static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    protected static string Headline(AlertNotification n) => n.Kind switch
    {
        AlertNotificationKind.Resolved => $"Resolved: {n.Title}",
        AlertNotificationKind.Reminder => $"Still open: {n.Title}",
        AlertNotificationKind.Test => "WatchTower test notification",
        _ => n.Title,
    };

    protected static List<(string Title, string Value)> Facts(AlertNotification n)
    {
        var facts = new List<(string, string)>
        {
            ("Server", n.Server),
            ("Alert", n.AlertKey),
            ("Severity", AlertText.Label(n.Severity)),
            (n.Kind == AlertNotificationKind.Resolved ? "Resolved at" : "Time", Utc(n.OccurredAtUtc)),
        };
        if (n.Duration is { } duration && n.Kind is AlertNotificationKind.Reminder or AlertNotificationKind.Resolved)
            facts.Add((n.Kind == AlertNotificationKind.Resolved ? "Lasted" : "Open for", AlertText.FormatDuration(duration)));
        if (n.RetiredReason is { } retired)
            facts.Add(("Retired", retired));
        if (n.Incident?.TopProcesses is { Count: > 0 } processes)
            facts.Add(("Top processes", string.Join(", ", processes.Select(p =>
                $"{p.Name} (PID {p.Pid}, {p.CpuPercent:F1}% CPU, {p.MemoryMb:F0} MB)"))));
        return facts;
    }
}

/// <summary>
/// Generic JSON webhook. With a secret, the body is signed: header
/// <c>X-WatchTower-Signature: sha256=&lt;lowercase hex HMAC-SHA256(secret, raw body)&gt;</c>.
/// </summary>
public sealed class WebhookNotificationChannel(IHttpClientFactory httpClientFactory) : HttpNotificationChannel(httpClientFactory)
{
    public const string SignatureHeader = "X-WatchTower-Signature";
    public const string EventHeader = "X-WatchTower-Event";

    public override NotificationChannelType Type => NotificationChannelType.Webhook;

    public override string BuildPayload(AlertNotification n) => JsonSerializer.Serialize(new WebhookPayload(
        Schema: "watchtower.alert.v1",
        Kind: n.Kind.ToString().ToLowerInvariant(),
        AlertKey: n.AlertKey,
        Severity: AlertText.Label(n.Severity),
        Title: n.Title,
        Description: n.Description,
        Server: n.Server,
        OccurredAt: DateTime.SpecifyKind(n.OccurredAtUtc, DateTimeKind.Utc),
        OpenedAt: n.OpenedAtUtc is { } opened ? DateTime.SpecifyKind(opened, DateTimeKind.Utc) : null,
        DurationSeconds: n.Duration is { } d ? (long)d.TotalSeconds : null,
        NotifyCount: n.NotifyCount,
        IncidentType: n.Incident?.Type.ToString(),
        TopProcesses: n.Incident?.TopProcesses is { Count: > 0 } processes
            ? processes.Select(p => new WebhookProcess(p.Name, p.Pid, Math.Round(p.CpuPercent, 1), Math.Round(p.MemoryMb))).ToList()
            : null,
        RetiredReason: n.RetiredReason), JsonOptions);

    protected override void AddHeaders(HttpRequestMessage request, NotificationChannel channel, AlertNotification notification, byte[] body)
    {
        request.Headers.Add(EventHeader, notification.Kind.ToString().ToLowerInvariant());
        if (!string.IsNullOrEmpty(channel.Secret))
            request.Headers.Add(SignatureHeader, Sign(channel.Secret, body));
    }

    /// <summary><c>sha256=</c> + lowercase hex HMAC-SHA256 of <paramref name="body"/>.</summary>
    public static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private sealed record WebhookPayload(
        string Schema,
        string Kind,
        string AlertKey,
        string Severity,
        string Title,
        string Description,
        string Server,
        DateTime OccurredAt,
        DateTime? OpenedAt,
        long? DurationSeconds,
        int NotifyCount,
        string? IncidentType,
        List<WebhookProcess>? TopProcesses,
        string? RetiredReason);

    private sealed record WebhookProcess(string Name, int Pid, double CpuPercent, double MemoryMb);
}

/// <summary>
/// Microsoft Teams via a Workflows (Power Automate) "post to a channel when a webhook request is
/// received" URL: a message with one Adaptive Card attachment.
/// </summary>
public sealed class TeamsNotificationChannel(IHttpClientFactory httpClientFactory) : HttpNotificationChannel(httpClientFactory)
{
    public override NotificationChannelType Type => NotificationChannelType.Teams;

    public override string BuildPayload(AlertNotification n)
    {
        var color = n.Kind switch
        {
            AlertNotificationKind.Resolved => "Good",
            AlertNotificationKind.Test => "Accent",
            _ => n.Severity switch
            {
                AlertSeverity.Critical => "Attention",
                AlertSeverity.Warning => "Warning",
                _ => "Accent",
            },
        };

        var facts = new JsonArray();
        foreach (var (title, value) in Facts(n))
            facts.Add(new JsonObject { ["title"] = title, ["value"] = EscapeMarkdown(value) });

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["msteams"] = new JsonObject { ["width"] = "Full" },
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "TextBlock", ["text"] = EscapeMarkdown(Headline(n)), ["weight"] = "Bolder", ["size"] = "Medium",
                    ["color"] = color, ["wrap"] = true,
                },
                new JsonObject { ["type"] = "TextBlock", ["text"] = EscapeMarkdown(n.Description), ["wrap"] = true },
                new JsonObject { ["type"] = "FactSet", ["facts"] = facts },
            },
        };

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = card,
                },
            },
        }.ToJsonString();
    }

    /// <summary>Characters with a Markdown meaning anywhere in a line.</summary>
    private const string MarkdownSpecials = "\\`*_~[](){}<>#|!";

    /// <summary>
    /// Backslash-escapes Markdown so text from a monitored server (service names, HTTP reason
    /// phrases, process names) renders literally in an Adaptive Card TextBlock or Fact: no links,
    /// emphasis, headings or lists. Escapes the punctuation in <see cref="MarkdownSpecials"/>
    /// everywhere, and list markers (<c>-</c>, <c>+</c>, <c>1.</c>) at the start of a line.
    /// </summary>
    public static string EscapeMarkdown(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var escaped = new StringBuilder(value.Length + 16);
        var lineStart = true; // only whitespace so far on this line
        var lineDigits = false; // only whitespace, then digits so far ("1." starts a numbered list)
        foreach (var c in value)
        {
            if (MarkdownSpecials.Contains(c) || (lineStart && c is '-' or '+' or '=') || (lineDigits && c == '.'))
                escaped.Append('\\');
            escaped.Append(c);

            if (c is '\n' or '\r')
            {
                lineStart = true;
                lineDigits = false;
            }
            else if (char.IsAsciiDigit(c) && (lineStart || lineDigits))
            {
                lineStart = false;
                lineDigits = true;
            }
            else if (!char.IsWhiteSpace(c) || lineDigits)
            {
                lineStart = false;
                lineDigits = false;
            }
        }
        return escaped.ToString();
    }
}

/// <summary>
/// Slack incoming webhook with Block Kit blocks. Text that comes from the monitored server is
/// never parsed as mrkdwn: the blocks carry it in <c>plain_text</c> objects (no links, mentions or
/// emphasis), and the notification fallback <c>text</c> is sent with <c>"mrkdwn": false</c> and
/// &amp;, &lt; and &gt; escaped (Slack's control characters, which also form links and mentions).
/// </summary>
public sealed class SlackNotificationChannel(IHttpClientFactory httpClientFactory) : HttpNotificationChannel(httpClientFactory)
{
    public override NotificationChannelType Type => NotificationChannelType.Slack;

    public override string BuildPayload(AlertNotification n)
    {
        var marker = n.Kind switch
        {
            AlertNotificationKind.Resolved => ":large_green_circle:",
            AlertNotificationKind.Test => ":information_source:",
            _ => n.Severity switch
            {
                AlertSeverity.Critical => ":red_circle:",
                AlertSeverity.Warning => ":large_orange_circle:",
                _ => ":large_blue_circle:",
            },
        };
        var headline = Headline(n);

        var fields = new JsonArray();
        foreach (var (title, value) in Facts(n).Take(10)) // Slack allows at most 10 fields per section
            fields.Add(PlainText($"{title}\n{Truncate(value, 1900)}", emoji: false));

        return new JsonObject
        {
            ["text"] = $"{marker} {Escape(headline)} ({Escape(n.Server)})",
            ["mrkdwn"] = false,
            ["blocks"] = new JsonArray
            {
                new JsonObject { ["type"] = "header", ["text"] = PlainText(Truncate(headline, 150), emoji: false) },
                new JsonObject { ["type"] = "section", ["text"] = PlainText($"{marker} {Truncate(n.Description, 2900)}", emoji: true) },
                new JsonObject { ["type"] = "section", ["fields"] = fields },
            },
        }.ToJsonString();
    }

    /// <summary>Slack control characters: &amp;, &lt; and &gt; must be escaped in message text.</summary>
    public static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>A text object Slack shows verbatim (no mrkdwn, links or mentions).</summary>
    private static JsonObject PlainText(string text, bool emoji) =>
        new() { ["type"] = "plain_text", ["text"] = text, ["emoji"] = emoji };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}