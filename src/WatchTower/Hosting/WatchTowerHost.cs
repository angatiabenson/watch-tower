using Serilog;

using WatchTower.Configuration;
using WatchTower.Configuration.Layered;
using WatchTower.Configuration.Settings;
using WatchTower.Ui;

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
    public static void ConfigureSources(
        IConfigurationBuilder cfg, WatchTowerPaths paths, string environmentName, bool reloadOnChange = true,
        System.Text.Json.Nodes.JsonObject? operatorSettingsOverride = null)
    {
        cfg.Sources.Clear();
        cfg.SetBasePath(paths.InstallDirectory);
        cfg.AddLayeredJsonFiles(
        [
            new ConfigLayerFile(paths.DefaultSettingsFilePath, ConfigLayer.Default),
            new ConfigLayerFile(Path.Combine(paths.InstallDirectory, $"appsettings.{environmentName}.json"), ConfigLayer.Default),
            new ConfigLayerFile(paths.SettingsFilePath, ConfigLayer.Operator, Protected: true, operatorSettingsOverride),
            new ConfigLayerFile(paths.RemoteConfigFilePath, ConfigLayer.Portal),
        ], reloadOnChange);
        cfg.AddEnvironmentVariables();
        cfg.AddEnvironmentVariables(prefix: "WATCHTOWER_");
    }

    /// <summary>
    /// Creates the host builder. When the web console is enabled and has at least one usable
    /// listen endpoint, this is a <see cref="WebApplicationBuilder"/>; otherwise a plain
    /// <see cref="HostApplicationBuilder"/> (so a misconfigured or busy console port never
    /// prevents monitoring from running).
    /// </summary>
    /// <param name="probeUiPorts">Check that console ports are free (production start-up only).</param>
    public static IHostApplicationBuilder CreateBuilder(
        WatchTowerPaths paths, string serviceName = DefaultServiceName, bool probeUiPorts = false)
    {
        var environmentName = ResolveEnvironmentName();

        // Peek at the configuration to decide whether the console runs.
        var probe = new ConfigurationBuilder();
        ConfigureSources(probe, paths, environmentName, reloadOnChange: false);
        var probeConfig = probe.Build();
        var ui = probeConfig.GetSection(UiOptions.SectionName).Get<UiOptions>() ?? new UiOptions();
        (probeConfig as IDisposable)?.Dispose();
        var (endpoints, _, certificate) = UiHosting.PlanEndpoints(ui, probeUiPorts);

        IHostApplicationBuilder builder;
        if (endpoints.Count > 0)
        {
            var web = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = paths.InstallDirectory,
                ApplicationName = DefaultServiceName,
                EnvironmentName = environmentName,
                // Static assets are embedded; never serve files from disk.
                WebRootPath = Path.Combine(paths.DataDirectory, "wwwroot-unused"),
            });
            web.AddWatchTowerUi(paths, ui, endpoints, certificate);
            builder = web;
        }
        else
        {
            builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = paths.InstallDirectory,
                ApplicationName = DefaultServiceName,
                EnvironmentName = environmentName,
            });
        }

        ConfigureSources(builder.Configuration, paths, environmentName);

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

    /// <summary>Builds the host from <see cref="CreateBuilder"/>, mapping the console when present.</summary>
    public static IHost Build(IHostApplicationBuilder builder) => builder switch
    {
        WebApplicationBuilder web => web.Build().UseWatchTowerUi(),
        HostApplicationBuilder plain => plain.Build(),
        _ => throw new ArgumentException($"Unsupported builder {builder.GetType()}", nameof(builder)),
    };

    /// <summary>
    /// Re-applies the SYSTEM + Administrators ACL on every start (not only on creation), so a
    /// directory created by an unprivileged tool or loosened later is always locked down
    /// before secrets are read. As a service the failure is fatal (fail closed).
    /// </summary>
    private static void EnforceDataDirectoryAcl(WatchTowerPaths paths, bool created)
    {
        var isService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
        if (!DataDirectorySecurity.IsPrivileged())
        {
            if (isService || created)
                Log.Warning("Running without administrative rights; the data directory {DataDirectory} " +
                            "permissions cannot be enforced", paths.DataDirectory);
            return;
        }

        if (DataDirectorySecurity.TryHarden(paths.DataDirectory, out var error))
            return;

        if (isService)
            throw new InvalidOperationException(
                $"Cannot restrict permissions on the data directory {paths.DataDirectory}: {error}");
        Log.Warning("Could not restrict data directory permissions: {Error}", error);
    }

    private static string ResolveEnvironmentName() =>
        new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
        ?? Environments.Production;

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

            EnforceDataDirectoryAcl(paths, dataDirCreated);

            var migration = LegacyStateMigrator.Run(paths);
            if (migration.Ran && migration.CopiedFiles.Count > 0)
                Log.Information("Imported legacy state from {InstallDirectory}: {Files}",
                    paths.InstallDirectory, string.Join(", ", migration.CopiedFiles));
            if (migration.Error is not null)
            {
                // Never start on a half-imported state: the stores would create an empty
                // database, and the import would then skip the real one forever. Exiting
                // non-zero lets the SCM recovery actions retry in a minute.
                throw new InvalidOperationException(
                    $"Importing state from the previous installation failed: {migration.Error}");
            }

            // Secrets hand-written into the settings file (or left by older builds) are
            // encrypted in place, and the plaintext backup copy is removed.
            if (await new JsonSettingsStore(paths.SettingsFilePath).ProtectPlaintextSecretsAsync())
                Log.Information("Encrypted plaintext secrets found in {SettingsFile}", paths.SettingsFilePath);

            var host = Build(CreateBuilder(paths, serviceName, probeUiPorts: true));
            var logger = host.Services.GetRequiredService<ILogger<AgentInfo>>();

            if (host is WebApplication)
                logger.LogInformation("Web console: {Urls}", string.Join(", ",
                    UiEndpointPlanner.Plan(host.Services.GetRequiredService<IConfiguration>()
                        .GetSection(UiOptions.SectionName).Get<UiOptions>() ?? new UiOptions()).Endpoints.Select(e => e.Display)));
            else
                logger.LogInformation("Web console is not running (disabled or no usable listen URL).");

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