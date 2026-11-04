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

    /// <summary>
    /// Read-modify-validate-write as one transaction: <paramref name="mutate"/> runs under the
    /// store lock against the current document and returns false to abort without writing.
    /// Use this when the change must be validated against the latest state (concurrent edits).
    /// </summary>
    Task<bool> TryUpdateAsync(Func<JsonObject, bool> mutate, CancellationToken ct = default);
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

    /// <summary>
    /// Encrypts any plaintext secret in the file (e.g. hand-edited, or carried over from an
    /// older build) and deletes the backup copy, which would still contain the plaintext.
    /// Returns true when something was encrypted.
    /// </summary>
    public async Task<bool> ProtectPlaintextSecretsAsync(CancellationToken ct = default)
    {
        // The backup is checked independently: another writer may already have encrypted the
        // primary file while an older backup still holds plaintext.
        var backup = FilePath + ".bak";
        if (File.Exists(backup) && !IsSafeBackup(backup))
            File.Delete(backup);

        if (!File.Exists(FilePath) || !ContainsPlaintextSecret(Read()))
            return false;

        await UpdateAsync(_ => { }, ct); // ProtectSecrets runs on every write
        return true;
    }

    private static bool IsSafeBackup(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), NodeOptions, ReadOptions) is JsonObject doc &&
                   !ContainsPlaintextSecret(doc);
        }
        catch (JsonException)
        {
            return false; // unreadable: cannot prove it holds no plaintext
        }
    }

    internal static bool ContainsPlaintextSecret(JsonObject root)
    {
        var found = false;
        Walk(root, null);
        return found;

        void Walk(JsonObject obj, string? prefix)
        {
            foreach (var (name, value) in obj)
                Visit(value, prefix is null ? name : $"{prefix}:{name}");
        }

        void Visit(JsonNode? value, string key)
        {
            switch (value)
            {
                case JsonObject child:
                    Walk(child, key);
                    break;
                case JsonArray array:
                    // List items are addressed by index, e.g. Notifications:Channels:0:Url.
                    for (var i = 0; i < array.Count; i++)
                        Visit(array[i], $"{key}:{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    break;
                case JsonValue leaf when SecretSettings.IsSecret(key) &&
                                         leaf.TryGetValue<string>(out var s) && s.Length > 0 && !DpapiSecretProtector.IsProtected(s):
                    found = true;
                    break;
            }
        }
    }

    public Task UpdateAsync(Action<JsonObject> mutate, CancellationToken ct = default) =>
        TryUpdateAsync(doc =>
        {
            mutate(doc);
            return true;
        }, ct);

    public async Task<bool> TryUpdateAsync(Func<JsonObject, bool> mutate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var crossProcessLock = await AcquireFileLockAsync(ct);

            var doc = Read();
            if (!mutate(doc))
                return false;
            ProtectSecrets(doc, _protector);
            WriteAtomically(doc);
            return true;
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
                    case JsonArray array:
                        WalkArray(array, key);
                        break;
                    case JsonValue leaf when NeedsProtection(key, leaf, out var s):
                        obj[name] = protector.Protect(s);
                        break;
                }
            }
        }

        // List items are addressed by index, e.g. Notifications:Channels:0:Url.
        void WalkArray(JsonArray array, string prefix)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var key = $"{prefix}:{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                switch (array[i])
                {
                    case JsonObject child:
                        Walk(child, key);
                        break;
                    case JsonArray nested:
                        WalkArray(nested, key);
                        break;
                    case JsonValue leaf when NeedsProtection(key, leaf, out var s):
                        array[i] = protector.Protect(s);
                        break;
                }
            }
        }

        static bool NeedsProtection(string key, JsonValue leaf, out string value)
        {
            value = string.Empty;
            if (!SecretSettings.IsSecret(key) || !leaf.TryGetValue<string>(out var s) ||
                string.IsNullOrEmpty(s) || DpapiSecretProtector.IsProtected(s))
                return false;
            value = s;
            return true;
        }
    }

    private void WriteAtomically(JsonObject doc)
    {
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, doc.ToJsonString(WriteOptions));
        WriteBackupOfCurrent();
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>
    /// Keeps the previous version as .bak — with its secrets encrypted, so a file that still held
    /// plaintext (hand-edited, legacy) never leaves a plaintext copy behind.
    /// </summary>
    private void WriteBackupOfCurrent()
    {
        var backup = FilePath + ".bak";
        if (!File.Exists(FilePath))
            return;
        try
        {
            var previous = Read();
            ProtectSecrets(previous, _protector);
            File.WriteAllText(backup, previous.ToJsonString(WriteOptions));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // The current file is unreadable, so it cannot be backed up safely.
            File.Delete(backup);
        }
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