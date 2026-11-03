namespace WatchTower.Portal.Contracts;

public record MetricsBatchDto(
    string AgentId,
    string BatchId,
    string BatchedAt,
    List<CpuMetricDto> CpuMetrics,
    List<RamMetricDto> RamMetrics,
    List<DiskMetricDto> DiskMetrics,
    List<DiskQueueMetricDto> DiskQueueMetrics,
    List<NetworkMetricDto> NetworkMetrics,
    List<ServiceStatusDto> ServiceStatuses,
    List<ApplicationPoolStatusDto> ApplicationPoolStatuses,
    AgentTelemetryDto? Agent = null,
    List<EndpointStatusDto>? EndpointStatuses = null
);

/// <summary>Agent self-description sent with every batch (omitted by older agents).</summary>
public record AgentTelemetryDto(
    string Version,
    string MachineName,
    string Os,
    string StartedAt,
    long UptimeSeconds,
    string UpdatePhase,
    string? AvailableVersion,
    AgentUpdateResultDto? LastUpdateResult,
    MaintenanceTelemetryDto? Maintenance = null
);

/// <summary>Maintenance window state (omitted by older agents). <c>UntilUtc</c> is ISO-8601 UTC.</summary>
public record MaintenanceTelemetryDto(bool Active, string? UntilUtc, string? Reason);

public record AgentUpdateResultDto(string ToVersion, bool Success, string CompletedAt);

public record CpuMetricDto(string Timestamp, double UsagePercent);

public record RamMetricDto(string Timestamp, double UsagePercent, double AvailableMb, double TotalMb);

public record DiskMetricDto(
    string Timestamp,
    string DriveName,
    double TotalGb,
    double FreeGb,
    double UsagePercent
);

public record DiskQueueMetricDto(string Timestamp, string InstanceName, double QueueLength);

public record NetworkMetricDto(string Timestamp, string InterfaceName, double OutputQueueLength);

public record ServiceStatusDto(
    string Timestamp,
    string ServiceName,
    string DisplayName,
    string Status,
    bool IsRunning
);

/// <summary>Latest endpoint check result (omitted when no endpoints are configured, and by older agents).</summary>
/// <param name="Type">Http | Tcp | Certificate.</param>
/// <param name="Target">Target without credentials or query string.</param>
/// <param name="Status">Up | Down | Expiring.</param>
public record EndpointStatusDto(
    string Timestamp,
    string Name,
    string Type,
    string Target,
    string Status,
    bool IsUp,
    double? ResponseTimeMs,
    double? CertificateDaysLeft,
    string? Detail
);

public record ApplicationPoolStatusDto(
    string Timestamp,
    string ApplicationPoolName,
    string Status,
    bool IsRunning
);