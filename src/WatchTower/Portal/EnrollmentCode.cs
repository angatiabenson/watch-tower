using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WatchTower.Portal;

/// <summary>
/// Pairing payload issued by the portal's "Add server" dialog, so connecting an agent is one
/// paste instead of four settings. Format: <c>WT1.</c> + base64url (no padding) of UTF-8 JSON
/// <c>{"v":1,"url":"…","agentId":"…","token":"…","requireTls":true}</c>.
/// </summary>
public sealed record EnrollmentCode(string Url, string AgentId, string Token, bool RequireTls)
{
    public const string Prefix = "WT1.";

    public static bool TryParse(string? text, out EnrollmentCode code, out string error)
    {
        code = null!;
        error = string.Empty;
        text = text?.Trim();

        if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return Fail("An enrollment code starts with \"WT1.\" — copy it again from the portal's Add server dialog.", out error);

        int? version;
        string? url, agentId, token;
        bool? requireTlsField;
        try
        {
            var payload = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))) as JsonObject
                       ?? throw new FormatException();
            version = json["v"]?.GetValue<int>();
            url = json["url"]?.GetValue<string>()?.Trim();
            agentId = json["agentId"]?.GetValue<string>()?.Trim();
            token = json["token"]?.GetValue<string>()?.Trim();
            requireTlsField = json["requireTls"]?.GetValue<bool>();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return Fail("The enrollment code is damaged (was it copied completely?).", out error);
        }

        if (version is not 1)
            return Fail("This enrollment code was made by a newer portal; update WatchTower first.", out error);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Fail("The enrollment code does not contain a valid portal URL.", out error);
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(token))
            return Fail("The enrollment code is missing the agent ID or token.", out error);

        var requireTls = requireTlsField ?? uri.Scheme == Uri.UriSchemeHttps;
        code = new EnrollmentCode(uri.AbsoluteUri.TrimEnd('/'), agentId, token, requireTls);
        return true;
    }

    /// <summary>Encodes a code (the portal does this; used here for tests and tooling).</summary>
    public string Encode()
    {
        var json = new JsonObject
        {
            ["v"] = 1,
            ["url"] = Url,
            ["agentId"] = AgentId,
            ["token"] = Token,
            ["requireTls"] = RequireTls,
        }.ToJsonString();
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The operator-settings patch that connects this agent to the portal.</summary>
    public JsonObject ToSettingsPatch() => new()
    {
        ["VisibilityPortal"] = new JsonObject
        {
            ["Enabled"] = true,
            // Portals pair agents over the HTTPS/JSON transport; never leave an agent on gRPC.
            ["Protocol"] = "Https",
            ["BaseUrl"] = Url,
            ["AgentId"] = AgentId,
            ["AgentToken"] = Token,
            ["Tls"] = new JsonObject { ["RequireTls"] = RequireTls },
        },
    };

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}