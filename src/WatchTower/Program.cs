using Serilog;
using WatchTower.Configuration;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting WatchTower service");

    var builder = Host.CreateDefaultBuilder(args)
        .UseWindowsService(o => o.ServiceName = "WatchTower")
        .UseSerilog((context, services, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration))
        .ConfigureServices((context, services) =>
        {
            var config = context.Configuration;

            // Configuration options
            services.Configure<CpuMonitorOptions>(config.GetSection(CpuMonitorOptions.SectionName));
            services.Configure<RamMonitorOptions>(config.GetSection(RamMonitorOptions.SectionName));
            services.Configure<DiskMonitorOptions>(config.GetSection(DiskMonitorOptions.SectionName));
            services.Configure<ServiceMonitorOptions>(config.GetSection(ServiceMonitorOptions.SectionName));
            services.Configure<HeartbeatOptions>(config.GetSection(HeartbeatOptions.SectionName));
            services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
            services.Configure<ReportingOptions>(config.GetSection(ReportingOptions.SectionName));

            // Services
            services.AddSingleton<IMetricsStore, MetricsStore>();
            services.AddSingleton<ISystemMetricsProvider, SystemMetricsProvider>();
            services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
            services.AddTransient<IEmailService, EmailService>();
            services.AddTransient<IReportBuilder, ReportBuilder>();

            // HTTP client for heartbeat
            services.AddHttpClient("Heartbeat", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            // Workers
            services.AddHostedService<CpuMonitorWorker>();
            services.AddHostedService<RamMonitorWorker>();
            services.AddHostedService<DiskMonitorWorker>();
            services.AddHostedService<ServiceMonitorWorker>();
            services.AddHostedService<HeartbeatWorker>();
            services.AddHostedService<EmailReporterWorker>();
        });

    var host = builder.Build();

    // Startup validation
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    var emailConfig = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<EmailOptions>>();
    var heartbeatConfig = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HeartbeatOptions>>();
    var serviceConfig = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ServiceMonitorOptions>>();

    if (string.IsNullOrWhiteSpace(emailConfig.CurrentValue.SmtpHost))
        logger.LogWarning("SMTP host is not configured. Email notifications will be disabled.");

    if (string.IsNullOrWhiteSpace(heartbeatConfig.CurrentValue.Url))
        logger.LogWarning("Heartbeat URL is not configured. Dead man's switch is disabled.");

    if (serviceConfig.CurrentValue.WatchedServices.Count == 0)
        logger.LogWarning("No watched services configured. Service monitoring will be idle.");

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
