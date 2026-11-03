namespace WatchTower.Services.Alerting;

/// <summary>
/// Identifies one alertable condition, e.g. <c>cpu</c>, <c>disk:C:\</c> or <c>svc:W3SVC</c>.
/// The key is persisted (<c>alert_state.key</c>) and sent to the portal as <c>alertKey</c>, so
/// the formats below are part of the wire contract. Keys compare case-insensitively.
/// </summary>
public readonly record struct AlertKey
{
    public AlertKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static AlertKey Cpu { get; } = new("cpu");
    public static AlertKey Ram { get; } = new("ram");

    /// <summary>One-shot "server restarted" notification.</summary>
    public static AlertKey Reboot { get; } = new("reboot");

    public static AlertKey Disk(string drive) => new($"disk:{drive}");
    public static AlertKey DiskQueue(string instance) => new($"diskqueue:{instance}");
    public static AlertKey Network(string interfaceName) => new($"net:{interfaceName}");
    public static AlertKey Service(string serviceName) => new($"svc:{serviceName}");
    public static AlertKey AppPool(string applicationPoolName) => new($"pool:{applicationPoolName}");
    public static AlertKey Endpoint(string endpointName) => new($"endpoint:{endpointName}");

    public override string ToString() => Value;
}