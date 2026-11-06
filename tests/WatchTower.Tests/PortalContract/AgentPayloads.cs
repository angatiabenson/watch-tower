using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Contracts;
using WatchTower.Updates;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// Realistic agent payloads. <c>Full*</c> sets every property (so strict validation covers every
/// field); <c>Minimal*</c> leaves every optional one out. Values and formatting follow the code that
/// builds them in the agent (OutboxDispatchWorker, AgentTelemetry, PortalEventPublisher,
/// CommandPollingWorker): timestamps are <c>DateTime.ToString("O")</c> in UTC, the process start and
/// update times carry an explicit offset.
/// </summary>
public static class AgentPayloads
{
    public static readonly DateTime Now = new(2026, 11, 3, 10, 15, 30, 123, DateTimeKind.Utc);

    public static string Utc(DateTime at) => at.ToString("O");

    public static string Utc(TimeSpan ago) => (Now - ago).ToString("O");

    public static MetricsBatchDto FullBatch(string agentId = AgentClientHarness.AgentId, string? batchId = null) => new(
        agentId,
        batchId ?? "0b6f3c1e-8f6c-4d8e-9a43-3c2a7d5e9b11",
        Utc(Now),
        [new CpuMetricDto(Utc(TimeSpan.FromSeconds(30)), 12.5), new CpuMetricDto(Utc(TimeSpan.FromSeconds(25)), 97.25)],
        [new RamMetricDto(Utc(TimeSpan.FromSeconds(30)), 61.21, 6355.4, 16383.5)],
        [
            new DiskMetricDto(Utc(TimeSpan.FromMinutes(1)), @"C:\", 126.4, 40.2, 68.2, IsStale: false),
            // A fixed drive that is not ready: still listed, last reading kept, older than its window.
            new DiskMetricDto(Utc(TimeSpan.FromDays(3)), @"D:\", 0, 0, 0, IsStale: true),
        ],
        [new DiskQueueMetricDto(Utc(TimeSpan.FromMinutes(1)), "0 C:", 0.4, IsStale: false)],
        [new NetworkMetricDto(Utc(TimeSpan.FromSeconds(30)), "Intel[R] 82574L Gigabit Network Connection", 0, IsStale: false)],
        [
            new ServiceStatusDto(Utc(TimeSpan.FromSeconds(20)), "W3SVC", "World Wide Web Publishing Service", "Running", true, IsStale: false),
            new ServiceStatusDto(Utc(TimeSpan.FromSeconds(20)), "NoSuchSvc", "NoSuchSvc", "NotFound", false, IsStale: false),
        ],
        [new ApplicationPoolStatusDto(Utc(TimeSpan.FromSeconds(20)), "DefaultAppPool", "Started", true, IsStale: false)],
        new AgentTelemetryDto(
            "1.6.0",
            "SRV-WEB-01",
            "Microsoft Windows 10.0.20348",
            new DateTimeOffset(Now.AddHours(-2), TimeSpan.Zero).ToString("O"),
            7230,
            nameof(UpdatePhase.Available),
            "1.7.0",
            new AgentUpdateResultDto("1.6.0", true, new DateTimeOffset(2026, 11, 3, 10, 59, 41, TimeSpan.FromHours(3)).ToString("O")),
            new MaintenanceTelemetryDto(true, "2026-11-03T18:00:00.0000000Z", "Patch Tuesday")),
        [
            new EndpointStatusDto(Utc(TimeSpan.FromSeconds(10)), "shop-home", "Http", "https://shop.example.com/", "Up", true,
                182.4, 61.4, "HTTP 200", IsStale: false),
            new EndpointStatusDto(Utc(TimeSpan.FromSeconds(10)), "old-cert", "Certificate", "legacy.example.com:443", "Down", false,
                40.0, -2.5, "Certificate expired 2.5 days ago", IsStale: false),
        ],
        [
            // Breaches inside their waiting period: not alerts, nothing sent yet.
            new PendingAlertDto("diskqueue:0 C:", "Warning", "Disk Queue High: 0 C: at 3.4",
                Utc(TimeSpan.FromMinutes(2)), 300, Utc(TimeSpan.FromMinutes(-3))),
            new PendingAlertDto("svc:Spooler", "Critical", "Service Down: Spooler",
                Utc(TimeSpan.FromSeconds(45)), 120, Utc(TimeSpan.FromSeconds(-75))),
        ]);

    /// <summary>A batch as the agent builds it with nothing optional: no telemetry provider, no endpoints.</summary>
    public static MetricsBatchDto MinimalBatch() => new(
        AgentClientHarness.AgentId, "4a1c0d2e-9b3f-4e5a-8c7d-6f1e2a3b4c5d", Utc(Now),
        [new CpuMetricDto(Utc(TimeSpan.FromSeconds(30)), 3.0)], [], [], [], [], [], []);

    /// <summary>Telemetry with every optional property left out (no update offered or installed, no window).</summary>
    public static AgentTelemetryDto MinimalTelemetry() => new(
        "0.0.0-dev", "SRV-WEB-01", "Microsoft Windows 10.0.20348",
        new DateTimeOffset(Now.AddMinutes(-5), TimeSpan.Zero).ToString("O"), 300, nameof(UpdatePhase.Idle), null, null,
        new MaintenanceTelemetryDto(false, null, null));

    public static AgentEventDto FullEvent(string eventId = "7d0c2f8e-1b5a-4f0e-8c3d-2a9e6b4f1c22") => new(
        eventId,
        Utc(TimeSpan.FromMinutes(5)),
        (int)IncidentType.CpuThresholdBreached,
        "CPU at 99.6% for over 300s",
        [
            new ProcessInfoDto("w3wp", 4412, 1890.2, 812.5, 71.3),
            new ProcessInfoDto("sqlservr", 2280, 9120.7, 4096, 19.9),
        ],
        "cpu",
        false);

    public static AgentEventDto MinimalEvent() => new(
        "5f2d8b3a-7c1e-4a9f-b6d0-3e8a1c5f7b66", Utc(TimeSpan.FromMinutes(1)), (int)IncidentType.ServiceDown,
        "Service 'World Wide Web Publishing Service' (W3SVC) is Stopped", null);

    public static AcknowledgeCommandDto Ack(string commandId) => new(commandId, Utc(Now));

    public static CommandResultDto Result(string commandId, bool success = true) =>
        new(commandId, success, success ? "Service 'W3SVC' restarted successfully" : "Not authorized: 'WinDefend' is not a locally watched service",
            Utc(Now));

    /// <summary>A command as a portal sends it, every property set.</summary>
    public static AgentCommandDto FullCommand(string commandId = "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10") => new(
        commandId, (int)ActionType.ServiceRestart,
        new Dictionary<string, string> { ["ServiceName"] = "W3SVC", ["TimeoutSeconds"] = "60" },
        "2026-11-03T23:00:00Z", Utc(TimeSpan.FromSeconds(5)));

    /// <summary>A remote configuration with every section and setting set.</summary>
    public static RemoteAgentConfigDto FullConfig() => new()
    {
        Cpu = new RemoteCpuConfig { PollIntervalSeconds = 5, ThresholdPercent = 90, SustainedSeconds = 600 },
        Ram = new RemoteRamConfig { PollIntervalSeconds = 5, ThresholdPercent = 92.5, SustainedSeconds = 300 },
        Disk = new RemoteDiskConfig
        {
            PollIntervalSeconds = 60,
            WarningThresholdPercent = 80,
            CriticalThresholdPercent = 95,
            QueueLengthWarningThreshold = 2,
            SustainedSeconds = 600,
            QueueSustainedSeconds = 240,
        },
        Network = new RemoteNetworkConfig { PollIntervalSeconds = 30, OutputQueueWarningThreshold = 10, SustainedSeconds = 300 },
        Services = new RemoteServiceConfig { PollIntervalSeconds = 30, WatchedServices = ["W3SVC", "MSSQL$SQLEXPRESS"], SustainedSeconds = 120 },
        AppPools = new RemoteAppPoolConfig { PollIntervalSeconds = 30, WatchedApplicationPools = ["DefaultAppPool", "Shop (v2)"], SustainedSeconds = 180 },
        Portal = new RemotePortalConfig
        {
            OutboxFlushIntervalSeconds = 30,
            CommandPollIntervalSeconds = 15,
            RemoteConfigPollIntervalSeconds = 300,
        },
        Maintenance = new RemoteMaintenanceConfig { UntilUtc = "2026-11-03T18:00:00Z", Reason = "Patch Tuesday" },
        Endpoints = new RemoteEndpointConfig { SustainedSeconds = 90 },
    };
}