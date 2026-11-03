using System.Text.Json;
using System.Text.Json.Nodes;

using WatchTower.Configuration.Secrets;

namespace WatchTower.Configuration.Settings;

/// <summary>
/// Reads and writes the operator settings file (<c>watchtower.settings.json</c>), the layer
/// the configuration UI and CLI manage. The file is sparse — it only needs to contain values
/// that differ from the shipped defaults — and every secret in it is DPAPI-encrypted.
/// </summary>
public interface ISettingsStore
{
    string FilePath { get; }

    /// <summary>Current file contents (secrets remain encrypted). Empty object when absent.</summary>
    JsonObject Read();

    /// <summary>Applies an RFC 7396 JSON merge patch (null removes a key) and persists atomically.</summary>
    Task ApplyMergePatchAsync(JsonObject patch, CancellationToken ct = default);

    /// <summary>Runs <paramref name="mutate"/> against the current document under the store lock and persists it.</summary>
    Task UpdateAsync(Action<JsonObject> mutate, CancellationToken ct = default);
}

public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string filePath, ISecretProtector? protector = null)
    {
        FilePath = filePath;
        _protector = protector ?? DpapiSecretProtector.Instance;
    }

    public string FilePath { get; }

    public JsonObject Read()
    {
        if (!File.Exists(FilePath))
            return new JsonObject(NodeOptions);

        var text = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject(NodeOptions);

        return JsonNode.Parse(text, NodeOptions, ReadOptions) as JsonObject
               ?? throw new InvalidDataException($"{FilePath} must contain a JSON object.");
    }

    public Task ApplyMergePatchAsync(JsonObject patch, CancellationToken ct = default) =>
        UpdateAsync(doc => MergePatch(doc, patch), ct);

    public async Task UpdateAsync(Action<JsonObject> mutate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var crossProcessLock = await AcquireFileLockAsync(ct);

            var doc = Read();
            mutate(doc);
            ProtectSecrets(doc, _protector);
            WriteAtomically(doc);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>RFC 7396 merge: objects merge recursively, null deletes, everything else replaces.</summary>
    public static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (name, patchValue) in patch.ToArray())
        {
            if (patchValue is null)
            {
                target.Remove(name);
                continue;
            }

            if (patchValue is JsonObject patchObject)
            {
                if (target[name] is not JsonObject targetObject)
                {
                    targetObject = new JsonObject(NodeOptions);
                    target[name] = targetObject;
                }
                MergePatch(targetObject, patchObject);
                continue;
            }

            target[name] = patchValue.DeepClone();
        }
    }

    /// <summary>Encrypts every non-empty, not-yet-protected string at a secret key path.</summary>
    public static void ProtectSecrets(JsonObject root, ISecretProtector protector)
    {
        Walk(root, prefix: null);

        void Walk(JsonObject obj, string? prefix)
        {
            foreach (var (name, value) in obj.ToArray())
            {
                var key = prefix is null ? name : $"{prefix}:{name}";
                switch (value)
                {
                    case JsonObject child:
                        Walk(child, key);
                        break;
                    case JsonValue leaf when SecretSettings.IsSecret(key)
                                             && leaf.TryGetValue<string>(out var s)
                                             && !string.IsNullOrEmpty(s)
                                             && !DpapiSecretProtector.IsProtected(s):
                        obj[name] = protector.Protect(s);
                        break;
                }
            }
        }
    }

    private void WriteAtomically(JsonObject doc)
    {
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, doc.ToJsonString(WriteOptions));
        if (File.Exists(FilePath))
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        File.Move(temp, FilePath, overwrite: true);
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        var lockPath = FilePath + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
        }
    }
}