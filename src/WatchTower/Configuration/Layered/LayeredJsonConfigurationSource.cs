using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

using WatchTower.Configuration.Secrets;
using WatchTower.Configuration.Settings;

namespace WatchTower.Configuration.Layered;

/// <summary>Which configuration layer a file represents.</summary>
public enum ConfigLayer
{
    Default,
    Operator,
    Portal,
}

/// <summary>
/// One JSON file participating in the layered configuration. When <paramref name="Content"/> is
/// set it is used instead of reading <paramref name="Path"/> (used to validate unsaved changes).
/// </summary>
public sealed record ConfigLayerFile(string Path, ConfigLayer Layer, bool Protected = false, JsonObject? Content = null);

/// <summary>
/// Merges several JSON files into one configuration provider using RFC 7396 merge-patch
/// semantics, lowest precedence first.
/// <para>
/// Why not one <c>AddJsonFile</c> per layer? The standard providers merge arrays <b>by index</b>,
/// so a higher layer setting <c>WatchedServices: ["X"]</c> over a lower <c>["A","B","C"]</c>
/// yields <c>["X","B","C"]</c> — removed entries silently stay active. Here a list in a higher
/// layer replaces the lower list entirely.
/// </para>
/// Protected layers have their <c>dpapi:</c> values decrypted on load. The provider also
/// records which layer supplied each key (used by the configuration UI).
/// </summary>
public sealed class LayeredJsonConfigurationSource : IConfigurationSource
{
    public required IReadOnlyList<ConfigLayerFile> Files { get; init; }
    public bool ReloadOnChange { get; init; } = true;
    public ISecretProtector Protector { get; init; } = DpapiSecretProtector.Instance;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new LayeredJsonConfigurationProvider(this);
}

public sealed class LayeredJsonConfigurationProvider : ConfigurationProvider, IDisposable
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly LayeredJsonConfigurationSource _source;
    private readonly List<IDisposable> _watchers = [];
    private readonly Lock _loadLock = new();
    private Dictionary<string, ConfigLayer> _origins = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _localData = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounce;

    public LayeredJsonConfigurationProvider(LayeredJsonConfigurationSource source)
    {
        _source = source;
        if (source.ReloadOnChange)
            StartWatching();
    }

    /// <summary>Secret keys that could not be decrypted on the last load (e.g. file copied from another machine).</summary>
    public ConcurrentDictionary<string, string> UndecryptableKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Error from the most recent reload attempt (previous values were kept), or null.</summary>
    public string? LastReloadError { get; private set; }

    /// <summary>Layer that supplied <paramref name="key"/> (or, for sections/lists, any of its descendants).</summary>
    public ConfigLayer? GetOrigin(string key)
    {
        var origins = _origins;
        if (origins.TryGetValue(key, out var layer))
            return layer;

        var prefix = key + ConfigurationPath.KeyDelimiter;
        ConfigLayer? best = null;
        foreach (var (k, l) in origins)
        {
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (best is null || l > best))
                best = l;
        }
        return best;
    }

    /// <summary>
    /// A string list as configured WITHOUT the portal overlay (shipped defaults + operator
    /// settings). Used where the portal must not be able to widen its own permissions, e.g. the
    /// allow-list of services a portal command may stop or restart.
    /// </summary>
    public IReadOnlyList<string> GetLocalList(string key)
    {
        var data = _localData;
        var prefix = key + ConfigurationPath.KeyDelimiter;
        return data
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(kv.Key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderBy(kv => int.Parse(kv.Key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture))
            .Select(kv => kv.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();
    }

    public override void Load()
    {
        // Initial load: malformed files are fatal so the operator gets a clear message.
        LoadCore(throwOnError: true);
    }

    private void LoadCore(bool throwOnError)
    {
        lock (_loadLock)
        {
            var merged = new JsonObject(JsonSettingsStore.NodeOptions);
            var local = new JsonObject(JsonSettingsStore.NodeOptions); // everything except the portal overlay
            var origins = new Dictionary<string, ConfigLayer>(StringComparer.OrdinalIgnoreCase);
            var undecryptable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var file in _source.Files)
                {
                    var doc = file.Content is not null
                        ? (JsonObject)file.Content.DeepClone()
                        : ReadFile(file.Path);
                    if (doc is null)
                        continue;

                    if (file.Protected)
                        Decrypt(doc, undecryptable);

                    foreach (var key in Flatten(doc).Keys)
                        origins[key] = file.Layer;

                    JsonSettingsStore.MergePatch(merged, doc);
                    if (file.Layer != ConfigLayer.Portal)
                        JsonSettingsStore.MergePatch(local, doc);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (throwOnError)
                    throw new InvalidDataException($"Failed to load configuration: {ex.Message}", ex);

                LastReloadError = ex.Message;
                return; // keep the last good configuration
            }

            Data = Flatten(merged);
            _origins = origins;
            _localData = Flatten(local);
            UndecryptableKeys.Clear();
            foreach (var (k, v) in undecryptable)
                UndecryptableKeys[k] = v;
            LastReloadError = null;
        }
    }

    private static JsonObject? ReadFile(string path)
    {
        if (!File.Exists(path))
            return null;

        string text;
        // Writers replace files atomically, but tolerate a brief sharing violation during a swap.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonNode.Parse(text, JsonSettingsStore.NodeOptions, ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"'{path}' must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    private void Decrypt(JsonObject doc, Dictionary<string, string> undecryptable)
    {
        Walk(doc, null);

        void Walk(JsonNode node, string? prefix)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj.ToArray())
                    {
                        var key = prefix is null ? name : $"{prefix}:{name}";
                        if (child is JsonValue v && v.TryGetValue<string>(out var s) && DpapiSecretProtector.IsProtected(s))
                        {
                            try
                            {
                                obj[name] = _source.Protector.Unprotect(s);
                            }
                            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
                            {
                                obj[name] = string.Empty; // never surface ciphertext as the secret
                                undecryptable[key] = ex.Message;
                            }
                        }
                        else if (child is not null)
                        {
                            Walk(child, key);
                        }
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                        if (arr[i] is { } item)
                            Walk(item, $"{prefix}:{i.ToString(CultureInfo.InvariantCulture)}");
                    break;
            }
        }
    }

    /// <summary>Flattens JSON into configuration keys exactly like the built-in JSON provider.</summary>
    internal static Dictionary<string, string?> Flatten(JsonObject root)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, child) in root)
            Visit(child, name);
        return data;

        void Visit(JsonNode? node, string key)
        {
            switch (node)
            {
                case null:
                    data[key] = null;
                    break;
                case JsonObject obj:
                    if (obj.Count == 0) { data[key] = null; break; }
                    foreach (var (name, child) in obj)
                        Visit(child, key + ConfigurationPath.KeyDelimiter + name);
                    break;
                case JsonArray arr:
                    if (arr.Count == 0) { data[key] = null; break; }
                    for (var i = 0; i < arr.Count; i++)
                        Visit(arr[i], key + ConfigurationPath.KeyDelimiter + i.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValue value:
                    data[key] = value.GetValueKind() switch
                    {
                        JsonValueKind.String => value.GetValue<string>(),
                        JsonValueKind.True => "True",
                        JsonValueKind.False => "False",
                        _ => value.ToJsonString(),
                    };
                    break;
            }
        }
    }

    private void StartWatching()
    {
        foreach (var directory in _source.Files.Select(f => System.IO.Path.GetDirectoryName(f.Path)!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;

            var provider = new PhysicalFileProvider(directory);
            _watchers.Add(provider);
            foreach (var file in _source.Files.Where(f =>
                         string.Equals(System.IO.Path.GetDirectoryName(f.Path), directory, StringComparison.OrdinalIgnoreCase)))
            {
                var name = System.IO.Path.GetFileName(file.Path);
                _watchers.Add(ChangeToken.OnChange(() => provider.Watch(name), ScheduleReload));
            }
        }
    }

    private void ScheduleReload()
    {
        // Debounce bursts of file system events (temp-file write + rename).
        var timer = new Timer(_ =>
        {
            LoadCore(throwOnError: false);
            OnReload();
        }, null, TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _debounce, timer)?.Dispose();
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        foreach (var w in _watchers)
            w.Dispose();
    }

    /// <summary>Finds the layered provider in a configuration root (null when absent).</summary>
    public static LayeredJsonConfigurationProvider? Find(IConfiguration configuration) =>
        (configuration as IConfigurationRoot)?.Providers.OfType<LayeredJsonConfigurationProvider>().FirstOrDefault();
}

public static class LayeredJsonConfigurationExtensions
{
    public static IConfigurationBuilder AddLayeredJsonFiles(
        this IConfigurationBuilder builder, IReadOnlyList<ConfigLayerFile> files, bool reloadOnChange = true,
        ISecretProtector? protector = null) =>
        builder.Add(new LayeredJsonConfigurationSource
        {
            Files = files,
            ReloadOnChange = reloadOnChange,
            Protector = protector ?? DpapiSecretProtector.Instance,
        });
}