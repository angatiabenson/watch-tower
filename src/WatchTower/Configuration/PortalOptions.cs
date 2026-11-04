namespace WatchTower.Configuration;

public class PortalOptions
{
    public const string SectionName = "VisibilityPortal";

    public bool Enabled { get; set; } = false;
    public string AgentId { get; set; } = string.Empty;
    public string AgentToken { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Protocol { get; set; } = "Https";

    public TlsOptions Tls { get; set; } = new();
    public OutboxOptions Outbox { get; set; } = new();
    public CommandPollOptions CommandPoll { get; set; } = new();
    public RemoteConfigOptions RemoteConfig { get; set; } = new();

    /// <summary>
    /// When true, commands without <c>issuedAt</c> are rejected ("Rejected: issuedAt is
    /// required"). False (default) accepts them, for portals written before <c>issuedAt</c>
    /// existed; their age cannot be checked, but a command id is still never executed twice.
    /// </summary>
    public bool RequireCommandIssuedAt { get; set; }

    /// <summary>
    /// How long every command id seen (executed, failed, rejected or not authorized) is
    /// remembered so a replay of it is never executed, independently of
    /// <see cref="CommandPollOptions.RetentionDays"/>. 0 removes the age limit
    /// (<see cref="CommandIdMaxCount"/> still applies).
    /// </summary>
    public int CommandIdRetentionDays { get; set; } = 365;

    /// <summary>At most this many command ids are remembered; the oldest are forgotten first. 0 removes the limit.</summary>
    public int CommandIdMaxCount { get; set; } = 200_000;
}

public class TlsOptions
{
    public bool RequireTls { get; set; } = true;
    public bool AllowUntrustedCert { get; set; } = false;
    public string? ClientCertThumbprint { get; set; }
}

public class OutboxOptions
{
    public int FlushIntervalSeconds { get; set; } = 30;
    public int MaxBatchSize { get; set; } = 500;
    public int MaxRetryAttempts { get; set; } = 5;
    public int RetryBackoffSeconds { get; set; } = 60;
    public int PurgeDeliveredAfterHours { get; set; } = 48;

    /// <summary>
    /// Incident events (AgentEvent) are retried indefinitely with capped exponential backoff and
    /// only dropped once they have waited this long undelivered (default one week).
    /// MaxRetryAttempts applies to metrics batches only.
    /// </summary>
    public int EventMaxAgeHours { get; set; } = 168;
}

public class CommandPollOptions
{
    public int PollIntervalSeconds { get; set; } = 30;
    public int ExecutionTimeoutSeconds { get; set; } = 120;

    /// <summary>Reject commands whose IssuedAt is older than this (replay protection). 0 disables the check.</summary>
    public int MaxCommandAgeSeconds { get; set; } = 3600;

    /// <summary>Tolerance for clock differences between agent and portal when validating IssuedAt.</summary>
    public int ClockSkewSeconds { get; set; } = 300;

    /// <summary>Completed/failed command rows are purged once older than this many days. 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// A command result the portal has not accepted is retried with a per-result backoff (1, 2,
    /// 4… up to 30 minutes) for this many hours after it was produced, then given up on (logged)
    /// and purged with the command history. Values below 1 count as 1.
    /// </summary>
    public int ResultRetryMaxHours { get; set; } = 168;

    /// <summary>
    /// When false (default), service/app-pool commands may only target names on the LOCALLY
    /// configured watch lists; lists pushed by the portal's remote configuration do not widen
    /// what the portal may stop or restart. Set true to let the portal manage both.
    /// </summary>
    public bool AllowPortalManagedTargets { get; set; }
}

public class RemoteConfigOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 300;
}