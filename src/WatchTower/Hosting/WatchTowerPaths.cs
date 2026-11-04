namespace WatchTower.Hosting;

/// <summary>
/// Single source of truth for every file system location WatchTower uses.
/// <para>
/// Binaries live in <see cref="InstallDirectory"/> and are treated as replaceable (the updater
/// and installer overwrite them). Everything mutable — database, settings, logs, update
/// staging — lives under <see cref="DataDirectory"/> so it survives upgrades untouched.
/// </para>
/// </summary>
public sealed class WatchTowerPaths
{
    /// <summary>Environment variable that overrides the data directory.</summary>
    public const string DataDirectoryEnvironmentVariable = "WATCHTOWER_DATA_DIR";

    public const string SettingsFileName = "watchtower.settings.json";
    public const string RemoteConfigFileName = "remote-config.json";
    public const string DatabaseFileName = "watchtower.db";
    public const string LastReportMarkerFileName = "last-report.txt";

    public WatchTowerPaths(string installDirectory, string dataDirectory)
    {
        InstallDirectory = Path.GetFullPath(installDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>Directory containing the executable and shipped defaults (appsettings.json).</summary>
    public string InstallDirectory { get; }

    /// <summary>Root of all mutable state (defaults to %ProgramData%\WatchTower).</summary>
    public string DataDirectory { get; }

    public string ConfigDirectory => Path.Combine(DataDirectory, "config");
    public string StateDirectory => Path.Combine(DataDirectory, "state");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    /// <summary>Operator settings written by the configuration UI / CLI (layered over appsettings.json).</summary>
    public string SettingsFilePath => Path.Combine(ConfigDirectory, SettingsFileName);

    /// <summary>Portal-managed overlay (layered over operator settings).</summary>
    public string RemoteConfigFilePath => Path.Combine(ConfigDirectory, RemoteConfigFileName);

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public string LastReportMarkerPath => Path.Combine(StateDirectory, LastReportMarkerFileName);
    public string LegacyMigrationMarkerPath => Path.Combine(StateDirectory, ".legacy-migrated");

    /// <summary>Shipped default configuration next to the executable.</summary>
    public string DefaultSettingsFilePath => Path.Combine(InstallDirectory, "appsettings.json");

    /// <summary>
    /// Resolves paths using, in order: an explicit override (e.g. <c>--data-dir</c>), the
    /// <see cref="DataDirectoryEnvironmentVariable"/> environment variable, a local <c>data</c>
    /// folder for Development runs, and finally <c>%ProgramData%\WatchTower</c>.
    /// </summary>
    public static WatchTowerPaths Resolve(string? dataDirectoryOverride = null, string? installDirectory = null)
    {
        var install = installDirectory ?? AppContext.BaseDirectory;

        var data = FirstNonEmpty(
            dataDirectoryOverride,
            Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable));

        if (data is null)
        {
            var environment = FirstNonEmpty(
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));

            data = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(install, "data")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "WatchTower");
        }

        return new WatchTowerPaths(install, Environment.ExpandEnvironmentVariables(data));
    }

    /// <summary>Creates the data directory tree. Returns true when the data root was newly created.</summary>
    public bool EnsureDirectories()
    {
        var created = !Directory.Exists(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
        return created;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}