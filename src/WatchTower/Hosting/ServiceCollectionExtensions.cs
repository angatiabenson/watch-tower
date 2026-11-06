using WatchTower.Configuration;
using WatchTower.Configuration.Settings;
using WatchTower.Portal;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Workers;

namespace WatchTower.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers every WatchTower option, service and worker (the composition root).</summary>
    public static IServiceCollection AddWatchTower(
        this IServiceCollection services, IConfiguration config, WatchTowerPaths paths)
    {
        // Give workers time to finish the current cycle / flush in-flight DB writes on stop.
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        // Single clock abstraction so worker timing is testable with a fake provider.
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(paths);
        services.AddSingleton<AgentInfo>();
        services.AddSingleton<IAgentRestarter, AgentRestarter>();
        services.AddSingleton<IWorkerHealth, WorkerHealthRegistry>();
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(paths.SettingsFilePath));

        AddOptions(services, config);

        // Storage. The outbox store also owns the portal sync-state cursors, which the daily
        // reporter resets after every purge — so it is required even in standalone mode.
        var dbPath = paths.DatabasePath;
        services.AddSingleton<IMetricsStore>(_ => new SqliteMetricsStore(dbPath));
        services.AddSingleton(_ => new SqliteOutboxStore(dbPath));
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<SqliteOutboxStore>());
        services.AddSingleton<IPortalSyncState>(sp => sp.GetRequiredService<SqliteOutboxStore>());
        services.AddSingleton<IAlertStateStore>(_ => new SqliteAlertStateStore(dbPath));

        services.AddSingleton<ISystemMetricsProvider, SystemMetricsProvider>();
        services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
        services.AddSingleton<IIisApplicationPoolManager, IisApplicationPoolManager>();
        services.AddTransient<IEmailService, EmailService>();
        services.AddTransient<IReportBuilder, ReportBuilder>();

        // Alerting: open/resolved lifecycle, re-notify policy and delivery.
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        services.AddSingleton<INotificationChannel, WebhookNotificationChannel>();
        services.AddSingleton<INotificationChannel, TeamsNotificationChannel>();
        services.AddSingleton<INotificationChannel, SlackNotificationChannel>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<INotificationDispatcher>(sp => sp.GetRequiredService<NotificationDispatcher>());
        // Webhook URLs are credentials: no redirects (the payload must not be forwarded) and no
        // HttpClient request logging (it would write the URL).
        services.AddHttpClient(HttpNotificationChannel.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(130))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<IMaintenanceState, MaintenanceState>();
        services.AddSingleton<PendingAlertTracker>();
        services.AddSingleton<IAlertManager, AlertManager>();
        services.AddHostedService<NotificationDeliveryWorker>();

        services.AddHttpClient("Heartbeat", client => client.Timeout = TimeSpan.FromSeconds(30));

        // Endpoint checks: per-check timeouts are applied by the probe; redirects are followed by
        // the probe itself (same host only), and request logging is off (targets may carry tokens).
        services.AddSingleton<Services.Endpoints.IEndpointProbe, Services.Endpoints.EndpointProbe>();
        services.AddHttpClient(Services.Endpoints.EndpointProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(130))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        // Self-update: feed client, state machine, periodic checker and the readiness beacon
        // the installer waits for after a restart.
        services.AddHttpClient(Updates.UpdateFeedClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<Updates.IUpdateFeedClient, Updates.UpdateFeedClient>();
        services.AddSingleton<Updates.UpdateService>();
        services.AddHostedService<Updates.UpdateWorker>();
        services.AddHostedService<HealthBeacon>();

        if (config.GetValue<bool>($"{PortalOptions.SectionName}:Enabled"))
            AddPortal(services, config, dbPath);
        else
            services.AddSingleton<IPortalEventPublisher, NullPortalEventPublisher>();

        services.AddHostedService<CpuMonitorWorker>();
        services.AddHostedService<RamMonitorWorker>();
        services.AddHostedService<DiskMonitorWorker>();
        services.AddHostedService<NetworkMonitorWorker>();
        services.AddHostedService<ServiceMonitorWorker>();
        services.AddHostedService<ApplicationPoolMonitorWorker>();
        services.AddHostedService<EndpointMonitorWorker>();
        // Freshness windows of the snapshot entries (metrics batches, console), and the warning
        // for a resource that is still listed but no longer read.
        services.AddSingleton<SnapshotFreshnessPolicy>();
        services.AddHostedService<StaleSnapshotWorker>();
        services.AddHostedService<HeartbeatWorker>();
        services.AddSingleton<IBootTimeProvider, TickCountBootTimeProvider>();
        services.AddHostedService<RebootDetectionWorker>();
        services.AddHostedService<EmailReporterWorker>();

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration config)
    {
        services.Configure<CpuMonitorOptions>(config.GetSection(CpuMonitorOptions.SectionName));
        services.Configure<RamMonitorOptions>(config.GetSection(RamMonitorOptions.SectionName));
        services.Configure<DiskMonitorOptions>(config.GetSection(DiskMonitorOptions.SectionName));
        services.Configure<ServiceMonitorOptions>(config.GetSection(ServiceMonitorOptions.SectionName));
        services.Configure<ApplicationPoolMonitorOptions>(config.GetSection(ApplicationPoolMonitorOptions.SectionName));
        services.Configure<NetworkMonitorOptions>(config.GetSection(NetworkMonitorOptions.SectionName));
        services.Configure<EndpointMonitorOptions>(config.GetSection(EndpointMonitorOptions.SectionName));
        services.Configure<HeartbeatOptions>(config.GetSection(HeartbeatOptions.SectionName));
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
        services.Configure<ReportingOptions>(config.GetSection(ReportingOptions.SectionName));
        services.Configure<AlertingOptions>(config.GetSection(AlertingOptions.SectionName));
        services.Configure<NotificationOptions>(config.GetSection(NotificationOptions.SectionName));
        services.Configure<MaintenanceOptions>(config.GetSection(MaintenanceOptions.SectionName));
        services.Configure<PortalOptions>(config.GetSection(PortalOptions.SectionName));
        services.Configure<DisplayOptions>(config.GetSection(DisplayOptions.SectionName));
        services.Configure<Ui.UiOptions>(config.GetSection(Ui.UiOptions.SectionName));
        services.Configure<Updates.UpdateOptions>(config.GetSection(Updates.UpdateOptions.SectionName));
    }

    private static void AddPortal(IServiceCollection services, IConfiguration config, string dbPath)
    {
        var protocol = config[$"{PortalOptions.SectionName}:Protocol"] ?? "Https";

        AddPortalHttpClient(services, config[$"{PortalOptions.SectionName}:BaseUrl"]);

        if (protocol.Equals("Grpc", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IPortalClient, GrpcPortalClient>();
        else
            services.AddSingleton<IPortalClient, HttpsPortalClient>();

        services.AddSingleton<ICommandStore>(_ => new SqliteCommandStore(dbPath));
        services.AddSingleton<IRemoteConfigStore>(_ => new SqliteRemoteConfigStore(dbPath));

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
        services.AddSingleton<IActionHandler, AgentUpdateHandler>();
        services.AddSingleton<IActionHandler, ApplyConfigurationHandler>();
        services.AddSingleton<IActionHandler, SendTestNotificationHandler>();
        services.AddSingleton<ActionHandlerRegistry>();
        services.AddSingleton<ICommandTargetPolicy, LocalCommandTargetPolicy>();
        services.AddSingleton<IAgentTelemetry, AgentTelemetry>();

        services.AddSingleton<IPortalEventPublisher, PortalEventPublisher>();

        services.AddHostedService<OutboxDispatchWorker>();
        services.AddHostedService<CommandPollingWorker>();

        if (config.GetValue($"{PortalOptions.SectionName}:RemoteConfig:Enabled", true))
            services.AddHostedService<RemoteConfigWorker>();
    }

    /// <summary>
    /// Registers the named <c>"PortalClient"</c> used by <see cref="HttpsPortalClient"/> (HTTPS and
    /// the opt-in plain HTTP): the portal TLS handler, which never follows redirects, and the
    /// credentials handler, which only signs requests below the configured BaseUrl.
    /// </summary>
    internal static IHttpClientBuilder AddPortalHttpClient(IServiceCollection services, string? configuredBaseUrl)
    {
        var baseUrl = (configuredBaseUrl ?? "").TrimEnd('/') + "/";

        services.AddTransient<AgentAuthHandler>();
        return services.AddHttpClient("PortalClient", client =>
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        // TLS policy (RequireTls), no redirects, and the optional client certificate
        // (Tls:ClientCertThumbprint) for mTLS.
        .ConfigurePrimaryHttpMessageHandler(sp => PortalTls.CreateHandler(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PortalOptions>>().CurrentValue.Tls))
        .AddHttpMessageHandler<AgentAuthHandler>();
    }
}