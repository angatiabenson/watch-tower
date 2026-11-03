using Serilog;

using WatchTower.Configuration;
using WatchTower.Configuration.Secrets;

namespace WatchTower.Hosting;

/// <summary>Builds and runs the WatchTower host (as a Windows service or a console app).</summary>
public static class WatchTowerHost
{
    public const string DefaultServiceName = "WatchTower";

    /// <summary>
    /// Configuration layers, lowest to highest precedence:
    /// <list type="number">
    ///   <item><c>appsettings.json</c> / <c>appsettings.{Environment}.json</c> — shipped defaults (install dir)</item>
    ///   <item><c>watchtower.settings.json</c> — operator settings from the UI/CLI (data dir, secrets encrypted)</item>
    ///   <item><c>remote-config.json</c> — portal-managed overlay (data dir)</item>
    ///   <item>Environment variables (unprefixed, then <c>WATCHTOWER_</c>-prefixed)</item>
    /// </list>
    /// </summary>
    public static void ConfigureSources(IConfigurationBuilder cfg, WatchTowerPaths paths, string environmentName)
    {
        cfg.Sources.Clear();
        cfg.SetBasePath(paths.InstallDirectory);
        cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        cfg.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);
        cfg.AddProtectedJsonFile(paths.SettingsFilePath, optional: true, reloadOnChange: true);
        cfg.AddJsonFile(
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(paths.ConfigDirectory),
            WatchTowerPaths.RemoteConfigFileName, optional: true, reloadOnChange: true);
        cfg.AddEnvironmentVariables();
        cfg.AddEnvironmentVariables(prefix: "WATCHTOWER_");
    }

    public static HostApplicationBuilder CreateBuilder(WatchTowerPaths paths, string serviceName = DefaultServiceName)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = paths.InstallDirectory,
            ApplicationName = DefaultServiceName,
        });

        ConfigureSources(builder.Configuration, paths, builder.Environment.EnvironmentName);

        builder.Services.AddWindowsService(o => o.ServiceName = serviceName);
        builder.Services.AddSingleton(new InstanceIdentity(serviceName));
        // Sink assemblies are passed explicitly: assembly scanning via "Serilog:Using" does not
        // work in single-file publishes.
        builder.Services.AddSerilog((_, lc) => lc.ReadFrom.Configuration(
            builder.Configuration,
            new Serilog.Settings.Configuration.ConfigurationReaderOptions(
                typeof(ConsoleLoggerConfigurationExtensions).Assembly,
                typeof(FileLoggerConfigurationExtensions).Assembly)));
        builder.Services.AddWatchTower(builder.Configuration, paths);

        return builder;
    }

    public static async Task<int> RunAsync(WatchTowerPaths paths, string serviceName = DefaultServiceName)
    {
        // Relative paths in configuration (notably the Serilog file sink's "Logs/…") resolve
        // into the data directory, so binaries stay replaceable. Windows services otherwise
        // start in C:\Windows\System32.
        var dataDirCreated = paths.EnsureDirectories();
        Directory.SetCurrentDirectory(paths.DataDirectory);

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "startup-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateBootstrapLogger();

        try
        {
            Log.Information("Starting WatchTower {Version} (data: {DataDirectory})",
                AgentInfo.ProductVersion, paths.DataDirectory);

            if (dataDirCreated && !DataDirectorySecurity.TryHarden(paths.DataDirectory, out var aclError) && aclError is not null)
                Log.Warning("Could not restrict data directory permissions: {Error}", aclError);

            var migration = LegacyStateMigrator.Run(paths);
            if (migration.Ran && migration.CopiedFiles.Count > 0)
                Log.Information("Imported legacy state from {InstallDirectory}: {Files}",
                    paths.InstallDirectory, string.Join(", ", migration.CopiedFiles));
            if (migration.Error is not null)
                Log.Warning("Legacy state import failed (will retry next start): {Error}", migration.Error);

            var host = CreateBuilder(paths, serviceName).Build();
            var logger = host.Services.GetRequiredService<ILogger<AgentInfo>>();

            var report = StartupValidator.Validate(host.Services);
            foreach (var warning in report.Warnings)
                logger.LogWarning("{Warning}", warning);
            if (!report.IsValid)
            {
                var message = "Invalid configuration:\n - " + string.Join("\n - ", report.Errors);
                logger.LogCritical("{Message}", message);
                throw new InvalidOperationException(message);
            }

            var portal = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PortalOptions>>().CurrentValue;
            if (portal.Enabled)
                logger.LogInformation(
                    "Visibility Portal enabled: AgentId={AgentId}, Protocol={Protocol}, BaseUrl={BaseUrl}, TokenConfigured={TokenConfigured}",
                    portal.AgentId, portal.Protocol, portal.BaseUrl, !string.IsNullOrEmpty(portal.AgentToken));
            else
                logger.LogInformation("Visibility Portal is disabled. Running in standalone mode.");

            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "WatchTower service terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}

/// <summary>Name of the Windows service this instance runs as (supports side-by-side installs).</summary>
public sealed record InstanceIdentity(string ServiceName);