namespace WatchTower.Portal.Contracts;

public class RemoteAgentConfigDto
{
    public RemoteCpuConfig? Cpu { get; set; }
    public RemoteRamConfig? Ram { get; set; }
    public RemoteDiskConfig? Disk { get; set; }
    public RemoteNetworkConfig? Network { get; set; }
    public RemoteServiceConfig? Services { get; set; }
    public RemoteAppPoolConfig? AppPools { get; set; }
    public RemotePortalConfig? Portal { get; set; }
}

public class RemoteCpuConfig
{
    public int? PollIntervalSeconds { get; set; }
    public double? ThresholdPercent { get; set; }
    public int? SustainedSeconds { get; set; }
}

public class RemoteRamConfig
{
    public int? PollIntervalSeconds { get; set; }
    public double? ThresholdPercent { get; set; }
    public int? SustainedSeconds { get; set; }
}

public class RemoteDiskConfig
{
    public int? PollIntervalSeconds { get; set; }
    public double? WarningThresholdPercent { get; set; }
    public double? CriticalThresholdPercent { get; set; }
    public double? QueueLengthWarningThreshold { get; set; }
}

public class RemoteNetworkConfig
{
    public int? PollIntervalSeconds { get; set; }
    public double? OutputQueueWarningThreshold { get; set; }
}

public class RemoteServiceConfig
{
    public int? PollIntervalSeconds { get; set; }
    public List<string>? WatchedServices { get; set; }
}

public class RemoteAppPoolConfig
{
    public int? PollIntervalSeconds { get; set; }
    public List<string>? WatchedApplicationPools { get; set; }
}

public class RemotePortalConfig
{
    public int? OutboxFlushIntervalSeconds { get; set; }
    public int? CommandPollIntervalSeconds { get; set; }
    public int? RemoteConfigPollIntervalSeconds { get; set; }
}
