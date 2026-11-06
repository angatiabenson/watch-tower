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
    List<EndpointStatusDto>? EndpointStatuses = null,
    List<PendingAlertDto>? PendingAlerts = null
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

// Snapshot entries (last-known reading per resource) carry IsStale: true when the reading is older
// than its freshness window when the batch is built (see Services.SnapshotFreshness), false
// otherwise. Null only in a batch queued by an agent that predates it (sent without isStale).

public record DiskMetricDto(
    string Timestamp,
    string DriveName,
    double TotalGb,
    double FreeGb,
    double UsagePercent,
    bool? IsStale = null
);

public record DiskQueueMetricDto(string Timestamp, string InstanceName, double QueueLength, bool? IsStale = null);

public record NetworkMetricDto(string Timestamp, string InterfaceName, double OutputQueueLength, bool? IsStale = null);

public record ServiceStatusDto(
    string Timestamp,
    string ServiceName,
    string DisplayName,
    string Status,
    bool IsRunning,
    bool? IsStale = null
);

/// <summary>Latest endpoint check result (omitted when no endpoints are configured, and by older agents).</summary>
/// <param name="Type">Http | Tcp | Certificate.</param>
/// <param name="Target">Target without credentials or query string.</param>
/// <param name="Status">
/// Up | Down | Expiring | ConfigurationError (open set). ConfigurationError: the check is
/// configured but invalid and not running; the other fields, isUp included, are its last result.
/// </param>
/// <param name="IsStale">Computed like every snapshot entry's, ConfigurationError included.</param>
public record EndpointStatusDto(
    string Timestamp,
    string Name,
    string Type,
    string Target,
    string Status,
    bool IsUp,
    double? ResponseTimeMs,
    double? CertificateDaysLeft,
    string? Detail,
    bool? IsStale = null
);

/// <summary>
/// A breach inside its alert's waiting period at <c>batchedAt</c> (sent by agents after 1.0.21; the
/// list is present, possibly empty, in every batch they send): nothing has been raised or sent.
/// Not an alert: a portal must not show it as one, nor count it as open.
/// </summary>
/// <param name="AlertKey">Key the alert will have if it is raised (same format as events' <c>alertKey</c>).</param>
/// <param name="Severity">Info | Warning | Critical (open set): the severity it will have.</param>
/// <param name="Title">Latest headline, e.g. "Disk Queue High: 0 C: at 3.4".</param>
/// <param name="Since">When the condition was first seen at this severity (ISO-8601 UTC).</param>
/// <param name="AlertAfterSeconds">The waiting period.</param>
/// <param name="AlertsAt">Since + AlertAfterSeconds: when it is raised if still present (ISO-8601 UTC).</param>
public record PendingAlertDto(
    string AlertKey,
    string Severity,
    string Title,
    string Since,
    int AlertAfterSeconds,
    string AlertsAt
);

public record ApplicationPoolStatusDto(
    string Timestamp,
    string ApplicationPoolName,
    string Status,
    bool IsRunning,
    bool? IsStale = null
);