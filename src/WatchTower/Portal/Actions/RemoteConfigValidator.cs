using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Actions;

/// <summary>
/// Validates portal-supplied configuration before it is persisted/applied. Every numeric
/// field that ends up driving a <c>Task.Delay</c> or a threshold is range-checked so a
/// malicious or buggy portal cannot push a 0/negative interval (which would hot-spin a
/// worker) or an out-of-range threshold.
/// </summary>
public static class RemoteConfigValidator
{
    public static bool Validate(RemoteAgentConfigDto config, out string error)
    {
        error = string.Empty;

        if (!Interval(config.Cpu?.PollIntervalSeconds, "Cpu.PollIntervalSeconds", ref error)) return false;
        if (!Percent(config.Cpu?.ThresholdPercent, "Cpu.ThresholdPercent", ref error)) return false;
        if (!Interval(config.Cpu?.SustainedSeconds, "Cpu.SustainedSeconds", ref error)) return false;

        if (!Interval(config.Ram?.PollIntervalSeconds, "Ram.PollIntervalSeconds", ref error)) return false;
        if (!Percent(config.Ram?.ThresholdPercent, "Ram.ThresholdPercent", ref error)) return false;
        if (!Interval(config.Ram?.SustainedSeconds, "Ram.SustainedSeconds", ref error)) return false;

        if (!Interval(config.Disk?.PollIntervalSeconds, "Disk.PollIntervalSeconds", ref error)) return false;
        if (!Percent(config.Disk?.WarningThresholdPercent, "Disk.WarningThresholdPercent", ref error)) return false;
        if (!Percent(config.Disk?.CriticalThresholdPercent, "Disk.CriticalThresholdPercent", ref error)) return false;

        if (!Interval(config.Network?.PollIntervalSeconds, "Network.PollIntervalSeconds", ref error)) return false;

        if (!Interval(config.Services?.PollIntervalSeconds, "Services.PollIntervalSeconds", ref error)) return false;
        if (!Interval(config.AppPools?.PollIntervalSeconds, "AppPools.PollIntervalSeconds", ref error)) return false;

        // Portal timing — these feed worker Task.Delay loops directly; a 0 would hot-spin.
        if (!Interval(config.Portal?.OutboxFlushIntervalSeconds, "Portal.OutboxFlushIntervalSeconds", ref error)) return false;
        if (!Interval(config.Portal?.CommandPollIntervalSeconds, "Portal.CommandPollIntervalSeconds", ref error)) return false;
        if (!Interval(config.Portal?.RemoteConfigPollIntervalSeconds, "Portal.RemoteConfigPollIntervalSeconds", ref error)) return false;

        return true;
    }

    private static bool Interval(int? value, string name, ref string error)
    {
        if (value.HasValue && value.Value < 1)
        {
            error = $"{name} must be >= 1";
            return false;
        }
        return true;
    }

    private static bool Percent(double? value, string name, ref string error)
    {
        if (value.HasValue && (value.Value < 1 || value.Value > 100))
        {
            error = $"{name} must be between 1 and 100";
            return false;
        }
        return true;
    }
}
