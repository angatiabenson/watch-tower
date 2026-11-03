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
}

public class CommandPollOptions
{
    public int PollIntervalSeconds { get; set; } = 30;
    public int ExecutionTimeoutSeconds { get; set; } = 120;

    /// <summary>Reject commands whose IssuedAt is older than this (replay protection). 0 disables the check.</summary>
    public int MaxCommandAgeSeconds { get; set; } = 3600;

    /// <summary>Tolerance for clock differences between agent and portal when validating IssuedAt.</summary>
    public int ClockSkewSeconds { get; set; } = 300;
}

public class RemoteConfigOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 300;
}