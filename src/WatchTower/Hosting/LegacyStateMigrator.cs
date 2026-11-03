using System.Text.Json;
using System.Text.Json.Nodes;

using WatchTower.Configuration.Settings;

namespace WatchTower.Hosting;

/// <summary>
/// One-time import of state written by WatchTower versions that stored everything next to
/// the executable (database, portal overlay, last-report marker and a hand-edited
/// appsettings.json). Files are <b>copied</b>, never moved, so rolling back to an older
/// build still finds its state. A marker file makes the import run at most once.
/// </summary>
public static class LegacyStateMigrator
{
    public sealed record Result(bool Ran, IReadOnlyList<string> CopiedFiles, string? Error);

    public static Result Run(WatchTowerPaths paths)
    {
        if (File.Exists(paths.LegacyMigrationMarkerPath))
            return new Result(false, [], null);

        // Running from the data directory itself (unusual dev setups): nothing to import.
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(paths.InstallDirectory),
                Path.TrimEndingDirectorySeparator(paths.DataDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            WriteMarker(paths);
            return new Result(false, [], null);
        }

        var copied = new List<string>();
        try
        {
            var legacyDb = Path.Combine(paths.InstallDirectory, WatchTowerPaths.DatabaseFileName);
            var isLegacyInstall = File.Exists(legacyDb);

            if (isLegacyInstall && !File.Exists(paths.DatabasePath))
            {
                // Copy the DB together with its WAL sidecars so un-checkpointed writes survive.
                // The main file goes last: its presence is what marks the import as done.
                foreach (var suffix in new[] { "-wal", "-shm", "" })
                    CopyIfPresent(legacyDb + suffix, paths.DatabasePath + suffix, copied);
            }

            CopyIfPresent(
                Path.Combine(paths.InstallDirectory, WatchTowerPaths.RemoteConfigFileName),
                paths.RemoteConfigFilePath, copied, skipIfTargetExists: true);

            CopyIfPresent(
                Path.Combine(paths.InstallDirectory, WatchTowerPaths.LastReportMarkerFileName),
                paths.LastReportMarkerPath, copied, skipIfTargetExists: true);

            // An existing deployment's appsettings.json was edited by hand. Carry it forward as
            // the operator settings layer so upgrades (which replace appsettings.json with
            // shipped defaults) never silently revert the configuration. Anything already in
            // the operator file (e.g. a seed imported by `install --config`) takes precedence.
            // Secrets are encrypted on the way in.
            if (isLegacyInstall && File.Exists(paths.DefaultSettingsFilePath))
            {
                var legacy = ParseObject(File.ReadAllText(paths.DefaultSettingsFilePath));
                new JsonSettingsStore(paths.SettingsFilePath).UpdateAsync(current =>
                {
                    var merged = (JsonObject)legacy.DeepClone();
                    JsonSettingsStore.MergePatch(merged, current);
                    current.Clear();
                    foreach (var (name, value) in merged.ToArray())
                    {
                        merged.Remove(name);
                        current[name] = value;
                    }
                }).GetAwaiter().GetResult();
                copied.Add(WatchTowerPaths.SettingsFileName + " (from appsettings.json)");
            }

            WriteMarker(paths);
            return new Result(true, copied, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException)
        {
            // Leave no marker so the import is retried on the next start.
            return new Result(true, copied, ex.Message);
        }
    }

    private static void CopyIfPresent(string source, string target, List<string> copied, bool skipIfTargetExists = false)
    {
        if (!File.Exists(source)) return;
        if (skipIfTargetExists && File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Copy to a temp name then rename, so an interrupted import never leaves a partially
        // written target that would make the retry skip the file.
        var temp = target + ".import";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
        copied.Add(Path.GetFileName(source));
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, JsonSettingsStore.NodeOptions,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
        ?? throw new InvalidDataException("Legacy appsettings.json must contain a JSON object.");

    private static void WriteMarker(WatchTowerPaths paths)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(paths.LegacyMigrationMarkerPath,
            $"Legacy state import completed {DateTime.UtcNow:O} from {paths.InstallDirectory}");
    }
}