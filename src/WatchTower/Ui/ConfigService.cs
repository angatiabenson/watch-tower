using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration.EnvironmentVariables;

using WatchTower.Configuration.Layered;
using WatchTower.Configuration.Settings;
using WatchTower.Hosting;

namespace WatchTower.Ui;

public sealed record FieldValueDto(JsonNode? Value, string Source, bool Overridden, bool HasValue);

public sealed record ConfigViewDto(
    IReadOnlyList<ConfigSection> Sections,
    IReadOnlyDictionary<string, FieldValueDto> Values,
    IReadOnlyList<string> Warnings);

public sealed class ConfigUpdateRequest
{
    public Dictionary<string, JsonElement> Changes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Reset { get; init; } = [];
}

public sealed record ConfigUpdateResult(
    bool Success,
    IReadOnlyDictionary<string, string> Errors,
    bool RestartRequired,
    IReadOnlyList<string> Warnings);

/// <summary>Tracks restart-required settings saved during this process lifetime.</summary>
public sealed class RestartState
{
    private volatile bool _pending;
    public bool Pending => _pending;
    public void MarkPending() => _pending = true;
}

/// <summary>
/// Reads the effective configuration for the console (with per-field provenance) and applies
/// edits to the operator settings layer after validating the resulting configuration as a whole.
/// </summary>
public sealed partial class ConfigService(
    IConfiguration configuration,
    ISettingsStore store,
    WatchTowerPaths paths,
    IHostEnvironment environment,
    RestartState restartState,
    ILogger<ConfigService> logger)
{
    public ConfigViewDto GetView()
    {
        var layered = LayeredJsonConfigurationProvider.Find(configuration);
        var envProviders = EnvironmentProviders();
        var values = new Dictionary<string, FieldValueDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in ConfigSchema.AllFields)
        {
            var fromEnv = envProviders.Any(p => Supplies(p, field.Key));
            var layer = layered?.GetOrigin(field.Key);
            var source = fromEnv ? "environment" : layer switch
            {
                ConfigLayer.Portal => "portal",
                ConfigLayer.Operator => "operator",
                _ => "default",
            };

            var value = ReadValue(configuration, field);
            var hasValue = value is not null && !(value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0)
                                             && !(value is JsonArray a && a.Count == 0);
            if (field.Type == FieldType.Secret)
                value = null; // write-only

            values[field.Key] = new FieldValueDto(value, source, Overridden: source is "portal" or "environment", hasValue);
        }

        var warnings = new List<string>();
        foreach (var key in layered?.UndecryptableKeys.Keys ?? Enumerable.Empty<string>())
            warnings.Add($"Secret '{key}' could not be decrypted on this machine (was the settings file copied from another server?). Re-enter it.");
        if (layered?.LastReloadError is { } err)
            warnings.Add($"The settings file on disk could not be loaded; the previous configuration is still active: {err}");

        return new ConfigViewDto(ConfigSchema.Sections, values, warnings);
    }

    public async Task<ConfigUpdateResult> ApplyAsync(ConfigUpdateRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, element) in request.Changes)
        {
            if (!ConfigSchema.TryGetField(key, out var field))
            {
                errors[key] = "Unknown setting.";
                continue;
            }
            if (field.Type == FieldType.ObjectList)
            {
                // Per-item errors are keyed "<key>:<index>[:<property>]" so the UI can show them inline.
                if (NormalizeObjectList(field, element, errors) is { } list)
                    normalized[field.Key] = list;
                continue;
            }
            if (TryNormalize(field, element, out var node, out var error))
                normalized[field.Key] = node;
            else
                errors[field.Key] = error!;
        }

        var resets = new List<ConfigField>();
        foreach (var key in request.Reset)
        {
            if (ConfigSchema.TryGetField(key, out var field))
                resets.Add(field);
            else
                errors[key] = "Unknown setting.";
        }

        if (errors.Count > 0)
            return new ConfigUpdateResult(false, errors, false, []);
        if (normalized.Count == 0 && resets.Count == 0)
            return new ConfigUpdateResult(true, errors, false, []);

        void Mutate(JsonObject doc)
        {
            foreach (var (key, node) in normalized)
                SetPath(doc, key, node?.DeepClone());
            foreach (var field in resets)
                RemovePath(doc, field.Key);
        }

        // Mutate, validate the configuration that WOULD result, and persist — all inside the
        // store transaction, so two concurrent edits can never each pass validation against a
        // stale document and together produce an invalid configuration.
        StartupValidator.Report report = new([], []);
        var restartRequired = false;
        var saved = await store.TryUpdateAsync(doc =>
        {
            Mutate(doc);
            var candidateConfig = BuildCandidate(doc);
            try
            {
                report = StartupValidator.Validate(candidateConfig);
                if (!report.IsValid)
                    return false;

                // Only flag a restart when a restart-bound setting's effective value really changes.
                restartRequired = normalized.Keys.Concat(resets.Select(r => r.Key))
                    .Select(k => ConfigSchema.TryGetField(k, out var f) ? f : null)
                    .Any(f => f is { RestartRequired: true } &&
                              !JsonNode.DeepEquals(ReadValue(configuration, f), ReadValue(candidateConfig, f)));
                return true;
            }
            finally
            {
                (candidateConfig as IDisposable)?.Dispose();
            }
        }, ct);

        if (!saved)
        {
            foreach (var message in report.Errors)
            {
                var (key, text) = MapError(message);
                errors[key] = text;
            }
            return new ConfigUpdateResult(false, errors, false, report.Warnings);
        }

        logger.LogInformation("Configuration updated from the console: {Keys}",
            string.Join(", ", normalized.Keys.Concat(resets.Select(r => r.Key + " (reset)"))));

        // Publish the change now instead of waiting for the debounced file watcher, so the
        // response — and the console's immediate re-read — reflect the saved values.
        try
        {
            (configuration as IConfigurationRoot)?.Reload();
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Saved settings could not be reloaded immediately; the file watcher will retry");
        }

        if (restartRequired)
            restartState.MarkPending();

        return new ConfigUpdateResult(true, errors, restartRequired, report.Warnings);
    }

    private IConfigurationRoot BuildCandidate(JsonObject operatorDoc)
    {
        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, paths, environment.EnvironmentName,
            reloadOnChange: false, operatorSettingsOverride: operatorDoc);
        return builder.Build();
    }

    internal static JsonNode? ReadValue(IConfiguration config, ConfigField field)
    {
        if (field.Type == FieldType.ObjectList)
        {
            // Children come back in numeric index order; absent properties show their default.
            var list = new JsonArray();
            foreach (var child in config.GetSection(field.Key).GetChildren())
            {
                if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    continue;
                var item = new JsonObject();
                foreach (var sub in field.Fields ?? [])
                    item[sub.Key] = ReadValue(child, sub) ?? JsonSerializer.SerializeToNode(sub.Default);
                list.Add(item);
            }
            return list;
        }

        if (ConfigSchema.IsListType(field.Type))
        {
            var items = config.GetSection(field.Key).Get<string[]>() ?? [];
            return new JsonArray(items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }

        var raw = config[field.Key];
        if (raw is null)
            return null;

        return field.Type switch
        {
            FieldType.Int when long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
            FieldType.Number when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
            FieldType.Bool when bool.TryParse(raw, out var b) => JsonValue.Create(b),
            _ => JsonValue.Create(raw),
        };
    }

    internal static bool TryNormalize(ConfigField field, JsonElement element, out JsonNode? node, out string? error)
    {
        node = null;
        error = null;

        string Range() => field.Min is { } min && field.Max is { } max
            ? $"Must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}."
            : "Out of range.";

        bool InRange(double v) => (field.Min is not { } lo || v >= lo) && (field.Max is not { } hi || v <= hi);

        switch (field.Type)
        {
            case FieldType.Int:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var l))
                    return Fail("Must be a whole number.", out error);
                if (!InRange(l)) return Fail(Range(), out error);
                node = JsonValue.Create(l);
                return true;

            case FieldType.Number:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var d) || !double.IsFinite(d))
                    return Fail("Must be a number.", out error);
                if (!InRange(d)) return Fail(Range(), out error);
                node = JsonValue.Create(d);
                return true;

            case FieldType.Bool:
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Fail("Must be true or false.", out error);
                node = JsonValue.Create(element.GetBoolean());
                return true;

            case FieldType.List or FieldType.Services or FieldType.AppPools:
                if (element.ValueKind != JsonValueKind.Array)
                    return Fail("Must be a list.", out error);
                var items = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return Fail("List entries must be text.", out error);
                    var s = item.GetString()!.Trim();
                    if (s.Length == 0 || items.Contains(s, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (!CheckFormat(field.Format, s, out error))
                        return false;
                    if (field.Type is FieldType.Services or FieldType.AppPools && !SafeNameRegex().IsMatch(s))
                        return Fail($"'{s}' contains characters that are not valid in a name.", out error);
                    items.Add(s);
                }
                node = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
                return true;

            case FieldType.ObjectList:
                var listErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                node = NormalizeObjectList(field, element, listErrors);
                if (node is null)
                    return Fail(listErrors.Values.FirstOrDefault() ?? "Invalid list.", out error);
                return true;
        }

        // String-like types.
        if (element.ValueKind != JsonValueKind.String)
            return Fail("Must be text.", out error);
        var text = element.GetString()!.Trim();
        if (text.Length > 2048)
            return Fail("Value is too long.", out error);
        // For text, Max is a length limit (used by list-item fields such as an endpoint name).
        if (field.Type == FieldType.String && field.Max is { } maxLength && text.Length > maxLength)
            return Fail($"Use at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.", out error);

        switch (field.Type)
        {
            case FieldType.Enum:
                var option = field.Options?.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                    return Fail($"Must be one of: {string.Join(", ", field.Options ?? [])}.", out error);
                text = option;
                break;
            case FieldType.Time:
                if (!TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    return Fail("Use 24-hour HH:mm, e.g. 08:00.", out error);
                text = t.ToString("HH:mm", CultureInfo.InvariantCulture);
                break;
            case FieldType.TimeZone:
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(text, out _))
                    return Fail("Unknown time zone.", out error);
                break;
            case FieldType.Url:
                if (text.Length > 0 && !CheckFormat("url", text, out error))
                    return false;
                break;
            default:
                if (text.Length > 0 && !CheckFormat(field.Format, text, out error))
                    return false;
                break;
        }

        node = JsonValue.Create(text);
        return true;
    }

    private static bool CheckFormat(string? format, string value, out string? error)
    {
        error = null;
        switch (format)
        {
            case "email" when !System.Net.Mail.MailAddress.TryCreate(value, out var addr) || addr.Address != value:
                error = $"'{value}' is not a valid email address.";
                return false;
            case "url" when !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps):
                error = $"'{value}' is not a valid http(s) URL.";
                return false;
            default:
                return true;
        }
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    /// <summary>
    /// Validates an <see cref="FieldType.ObjectList"/> value: an array (at most <c>Max</c> entries)
    /// of objects with only the schema's item properties, required properties present, every
    /// property valid for its type and the <c>UniqueBy</c> property unique. Absent optional
    /// properties are left out so the options class default applies. Errors are added to
    /// <paramref name="errors"/> keyed <c>&lt;key&gt;:&lt;index&gt;[:&lt;property&gt;]</c>; returns null when any failed.
    /// </summary>
    internal static JsonArray? NormalizeObjectList(ConfigField field, JsonElement element, IDictionary<string, string> errors)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors[field.Key] = "Must be a list.";
            return null;
        }
        if (field.Max is { } max && element.GetArrayLength() > max)
        {
            errors[field.Key] = $"At most {max.ToString(CultureInfo.InvariantCulture)} entries are allowed.";
            return null;
        }

        var itemFields = field.Fields ?? [];
        var result = new JsonArray();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ok = true;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var prefix = $"{field.Key}:{index.ToString(CultureInfo.InvariantCulture)}";
            var position = index + 1;
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors[prefix] = $"Entry {position} must be an object.";
                ok = false;
                continue;
            }

            var provided = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.EnumerateObject())
            {
                var sub = itemFields.FirstOrDefault(f => string.Equals(f.Key, property.Name, StringComparison.OrdinalIgnoreCase));
                if (sub is null)
                {
                    errors[prefix] = $"Entry {position}: unknown property '{Truncate(property.Name, 64)}'.";
                    ok = false;
                    continue;
                }
                provided[sub.Key] = property.Value;
            }

            var normalized = new JsonObject(JsonSettingsStore.NodeOptions);
            foreach (var sub in itemFields)
            {
                var key = $"{prefix}:{sub.Key}";
                if (!provided.TryGetValue(sub.Key, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    if (sub.Required)
                    {
                        errors[key] = $"{sub.Label} is required.";
                        ok = false;
                    }
                    continue;
                }
                if (!TryNormalize(sub, value, out var node, out var error))
                {
                    errors[key] = error!;
                    ok = false;
                    continue;
                }
                if (sub.Required && node is JsonValue text && text.TryGetValue<string>(out var s) && s.Length == 0)
                {
                    errors[key] = $"{sub.Label} is required.";
                    ok = false;
                    continue;
                }
                normalized[sub.Key] = node;
            }

            if (field.UniqueBy is { } unique &&
                normalized[unique] is JsonValue uniqueValue && uniqueValue.TryGetValue<string>(out var name) && name.Length > 0)
            {
                if (seen.TryGetValue(name, out var first))
                {
                    var label = itemFields.FirstOrDefault(f => f.Key == unique)?.Label ?? unique;
                    errors[$"{prefix}:{unique}"] =
                        $"{label} '{name}' is already used by entry {first.ToString(CultureInfo.InvariantCulture)}. Each {label.ToLowerInvariant()} must be unique.";
                    ok = false;
                }
                else
                {
                    seen[name] = position;
                }
            }
            result.Add(normalized);
        }
        return ok ? result : null;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    /// <summary>
    /// Maps a validator message back to the schema key it mentions (for inline errors). A
    /// message about one entry of an object list ("Monitoring:Endpoints:2:Target must be …")
    /// maps to that entry's key ("Monitoring:Endpoints:2:Target") and loses the key prefix.
    /// </summary>
    internal static (string Key, string Message) MapError(string message)
    {
        foreach (var field in ConfigSchema.AllFields.Where(f => f.Type == FieldType.ObjectList))
        {
            var prefix = field.Key + ":";
            if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = message[prefix.Length..];
            var digits = rest.TakeWhile(char.IsAsciiDigit).Count();
            if (digits == 0)
                break;
            var key = field.Key + ":" + rest[..digits];
            rest = rest[digits..];
            if (rest.StartsWith(':'))
            {
                var name = new string(rest[1..].TakeWhile(char.IsAsciiLetterOrDigit).ToArray());
                if ((field.Fields ?? []).FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)) is { } sub)
                    return ($"{key}:{sub.Key}", rest[1..]);
                return (key, rest[1..]);
            }
            return (key, rest.TrimStart());
        }

        var normalized = message.Replace('.', ':');
        var match = ConfigSchema.AllFields
            .Select(f => f.Key)
            .Where(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        return (match ?? "_", message);
    }

    internal static void SetPath(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split(':');
        var current = root;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject next)
            {
                next = new JsonObject(JsonSettingsStore.NodeOptions);
                current[part] = next;
            }
            current = next;
        }
        current[parts[^1]] = value;
    }

    internal static void RemovePath(JsonObject root, string key)
    {
        var parts = key.Split(':');
        var chain = new List<JsonObject> { root };
        foreach (var part in parts[..^1])
        {
            if (chain[^1][part] is not JsonObject next)
                return;
            chain.Add(next);
        }
        chain[^1].Remove(parts[^1]);

        // Prune now-empty parents so the file stays tidy.
        for (var i = chain.Count - 1; i > 0; i--)
        {
            if (chain[i].Count > 0) break;
            chain[i - 1].Remove(parts[i - 1]);
        }
    }

    private List<EnvironmentVariablesConfigurationProvider> EnvironmentProviders() =>
        (configuration as IConfigurationRoot)?.Providers.OfType<EnvironmentVariablesConfigurationProvider>().ToList() ?? [];

    private static bool Supplies(IConfigurationProvider provider, string key) =>
        provider.TryGet(key, out _) || provider.GetChildKeys([], key).Any();

    [GeneratedRegex(@"^[\w .\-$@()]{1,256}$")]
    private static partial Regex SafeNameRegex();
}