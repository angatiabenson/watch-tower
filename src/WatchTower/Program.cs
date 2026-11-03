using Serilog;

using WatchTower.Configuration;
using WatchTower.Portal;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

// Set working directory to the exe's directory so relative paths (logs, config) resolve correctly
// Windows Services default to C:\Windows\System32 as working directory
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    Log.Information("Starting WatchTower service");

    var builder = Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        .UseWindowsService(o => o.ServiceName = "WatchTower")
        .UseSerilog(
            (context, services, configuration) =>
                configuration.ReadFrom.Configuration(context.Configuration)
        )
        .ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "remote-config.json"),
                optional: true,
                reloadOnChange: true);
        })
        .ConfigureServices(
            (context, services) =>
            {
                var config = context.Configuration;

                // Give workers time to finish the current cycle / flush in-flight DB writes on stop.
                services.Configure<HostOptions>(o =>
                {
                    o.ShutdownTimeout = TimeSpan.FromSeconds(30);
                });

                // Single clock abstraction so worker timing is testable with a fake provider.
                services.AddSingleton(TimeProvider.System);

                // Configuration options
                services.Configure<CpuMonitorOptions>(
                    config.GetSection(CpuMonitorOptions.SectionName)
                );
                services.Configure<RamMonitorOptions>(
                    config.GetSection(RamMonitorOptions.SectionName)
                );
                services.Configure<DiskMonitorOptions>(
                    config.GetSection(DiskMonitorOptions.SectionName)
                );
                services.Configure<ServiceMonitorOptions>(
                    config.GetSection(ServiceMonitorOptions.SectionName)
                );
                services.Configure<ApplicationPoolMonitorOptions>(
                    config.GetSection(ApplicationPoolMonitorOptions.SectionName)
                );
                services.Configure<NetworkMonitorOptions>(
                    config.GetSection(NetworkMonitorOptions.SectionName)
                );
                services.Configure<HeartbeatOptions>(
                    config.GetSection(HeartbeatOptions.SectionName)
                );
                services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
                services.Configure<ReportingOptions>(
                    config.GetSection(ReportingOptions.SectionName)
                );
                services.Configure<PortalOptions>(
                    config.GetSection(PortalOptions.SectionName)
                );
                services.Configure<DisplayOptions>(
                    config.GetSection(DisplayOptions.SectionName)
                );

                // Services
                var dbPath = Path.Combine(AppContext.BaseDirectory, "watchtower.db");
                services.AddSingleton<IMetricsStore>(new SqliteMetricsStore(dbPath));
                services.AddSingleton<ISystemMetricsProvider, SystemMetricsProvider>();
                services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
                services.AddSingleton<IIisApplicationPoolManager, IisApplicationPoolManager>();
                services.AddTransient<IEmailService, EmailService>();
                services.AddTransient<IReportBuilder, ReportBuilder>();

                // HTTP client for heartbeat
                services.AddHttpClient(
                    "Heartbeat",
                    client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                    }
                );

                // Visibility Portal
                var portalEnabled = config.GetValue<bool>($"{PortalOptions.SectionName}:Enabled");
                if (portalEnabled)
                {
                    var protocol = config[$"{PortalOptions.SectionName}:Protocol"] ?? "Https";
                    var baseUrl = (config[$"{PortalOptions.SectionName}:BaseUrl"] ?? "").TrimEnd('/') + "/";

                    services.AddTransient<AgentAuthHandler>();
                    services.AddHttpClient("PortalClient", client =>
                    {
                        client.BaseAddress = new Uri(baseUrl);
                        client.Timeout = TimeSpan.FromSeconds(30);
                    }).AddHttpMessageHandler<AgentAuthHandler>();

                    if (protocol.Equals("Grpc", StringComparison.OrdinalIgnoreCase))
                        services.AddSingleton<IPortalClient, GrpcPortalClient>();
                    else
                        services.AddSingleton<IPortalClient, HttpsPortalClient>();

                    services.AddSingleton<SqliteOutboxStore>(
                        _ => new SqliteOutboxStore(dbPath));
                    services.AddSingleton<IOutboxStore>(
                        sp => sp.GetRequiredService<SqliteOutboxStore>());
                    services.AddSingleton<IPortalSyncState>(
                        sp => sp.GetRequiredService<SqliteOutboxStore>());
                    services.AddSingleton<ICommandStore>(
                        _ => new SqliteCommandStore(dbPath));
                    services.AddSingleton<IRemoteConfigStore>(
                        _ => new SqliteRemoteConfigStore(dbPath));

                    services.AddSingleton<IMetricSnapshotTrigger, MetricSnapshotTrigger>();
                    services.AddSingleton<IConfigSyncTrigger, ConfigSyncTrigger>();

                    services.AddSingleton<IActionHandler, ServiceStartHandler>();
                    services.AddSingleton<IActionHandler, ServiceStopHandler>();
                    services.AddSingleton<IActionHandler, ServiceRestartHandler>();
                    services.AddSingleton<IActionHandler, AppPoolStartHandler>();
                    services.AddSingleton<IActionHandler, AppPoolStopHandler>();
                    services.AddSingleton<IActionHandler, AppPoolRestartHandler>();
                    services.AddSingleton<IActionHandler, AppPoolRecycleHandler>();
                    services.AddSingleton<IActionHandler, ForceMetricSnapshotHandler>();
                    services.AddSingleton<IActionHandler, ForceConfigSyncHandler>();
                    services.AddSingleton<IActionHandler, AgentRestartHandler>();
                    services.AddSingleton<IActionHandler, ApplyConfigurationHandler>();
                    services.AddSingleton<ActionHandlerRegistry>();

                    services.AddSingleton<IPortalEventPublisher, PortalEventPublisher>();

                    services.AddHostedService<OutboxDispatchWorker>();
                    services.AddHostedService<CommandPollingWorker>();

                    var remoteConfigEnabled = config.GetValue<bool>(
                        $"{PortalOptions.SectionName}:RemoteConfig:Enabled");
                    if (remoteConfigEnabled)
                        services.AddHostedService<RemoteConfigWorker>();
                }
                else
                {
                    services.AddSingleton<IPortalEventPublisher, NullPortalEventPublisher>();
                }

                // Workers
                services.AddHostedService<CpuMonitorWorker>();
                services.AddHostedService<RamMonitorWorker>();
                services.AddHostedService<DiskMonitorWorker>();
                services.AddHostedService<NetworkMonitorWorker>();
                services.AddHostedService<ServiceMonitorWorker>();
                services.AddHostedService<ApplicationPoolMonitorWorker>();
                services.AddHostedService<HeartbeatWorker>();
                services.AddHostedService<EmailReporterWorker>();
            }
        );

    var host = builder.Build();

    // Startup validation
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    var emailConfig =
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<EmailOptions>>();
    var heartbeatConfig =
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HeartbeatOptions>>();
    var serviceConfig =
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ServiceMonitorOptions>>();
    var applicationPoolConfig =
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ApplicationPoolMonitorOptions>>();
    var portalConfig =
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PortalOptions>>();

    // Fail fast on configuration that would otherwise create a "zombie" service
    // (e.g. a zero/negative poll interval busy-loops or error-spins a worker forever).
    var fatalConfigErrors = new List<string>();

    void RequirePositive(string name, int value)
    {
        if (value < 1) fatalConfigErrors.Add($"{name} must be >= 1 (was {value}).");
    }

    var cpu = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CpuMonitorOptions>>().CurrentValue;
    var ram = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RamMonitorOptions>>().CurrentValue;
    var disk = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DiskMonitorOptions>>().CurrentValue;
    var network = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<NetworkMonitorOptions>>().CurrentValue;
    var heartbeat = heartbeatConfig.CurrentValue;

    RequirePositive("Monitoring:Cpu:PollIntervalSeconds", cpu.PollIntervalSeconds);
    RequirePositive("Monitoring:Ram:PollIntervalSeconds", ram.PollIntervalSeconds);
    RequirePositive("Monitoring:Disk:PollIntervalSeconds", disk.PollIntervalSeconds);
    RequirePositive("Monitoring:Network:PollIntervalSeconds", network.PollIntervalSeconds);
    RequirePositive("Monitoring:Services:PollIntervalSeconds", serviceConfig.CurrentValue.PollIntervalSeconds);
    RequirePositive("Monitoring:ApplicationPools:PollIntervalSeconds", applicationPoolConfig.CurrentValue.PollIntervalSeconds);
    RequirePositive("Heartbeat:IntervalSeconds", heartbeat.IntervalSeconds);

    var portalOpts = portalConfig.CurrentValue;
    if (portalOpts.Enabled)
    {
        if (string.IsNullOrWhiteSpace(portalOpts.AgentId))
            fatalConfigErrors.Add("VisibilityPortal.AgentId is required when the portal is enabled.");
        if (string.IsNullOrWhiteSpace(portalOpts.AgentToken))
            fatalConfigErrors.Add("VisibilityPortal.AgentToken is required when the portal is enabled.");
        if (string.IsNullOrWhiteSpace(portalOpts.BaseUrl))
            fatalConfigErrors.Add("VisibilityPortal.BaseUrl is required when the portal is enabled.");
        RequirePositive("VisibilityPortal:Outbox:FlushIntervalSeconds", portalOpts.Outbox.FlushIntervalSeconds);
        RequirePositive("VisibilityPortal:CommandPoll:PollIntervalSeconds", portalOpts.CommandPoll.PollIntervalSeconds);
        RequirePositive("VisibilityPortal:RemoteConfig:PollIntervalSeconds", portalOpts.RemoteConfig.PollIntervalSeconds);
    }

    if (fatalConfigErrors.Count > 0)
    {
        var message = "Invalid configuration:\n - " + string.Join("\n - ", fatalConfigErrors);
        logger.LogCritical("{Message}", message);
        throw new InvalidOperationException(message);
    }

    if (string.IsNullOrWhiteSpace(emailConfig.CurrentValue.SmtpHost))
        logger.LogWarning("SMTP host is not configured. Email notifications will be disabled.");

    if (string.IsNullOrWhiteSpace(heartbeatConfig.CurrentValue.Url))
        logger.LogWarning("Heartbeat URL is not configured. Dead man's switch is disabled.");

    if (serviceConfig.CurrentValue.WatchedServices.Count == 0)
        logger.LogWarning("No watched services configured. Service monitoring will be idle.");

    if (applicationPoolConfig.CurrentValue.WatchedApplicationPools.Count == 0)
        logger.LogWarning("No watched IIS application pools configured. Application pool monitoring will be idle.");

    if (portalOpts.Enabled)
    {
        logger.LogInformation(
            "Visibility Portal enabled: AgentId={AgentId}, Protocol={Protocol}, BaseUrl={BaseUrl}, TokenConfigured={TokenConfigured}",
            portalOpts.AgentId,
            portalOpts.Protocol,
            portalOpts.BaseUrl,
            !string.IsNullOrEmpty(portalOpts.AgentToken));
    }
    else
    {
        logger.LogInformation("Visibility Portal is disabled. Running in standalone mode.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WatchTower service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}