using System.Text.RegularExpressions;

using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Actions;

/// <summary>
/// Validates portal-supplied configuration before it is persisted/applied. Every numeric
/// field that ends up driving a <c>Task.Delay</c> or a threshold is range-checked so a
/// malicious or buggy portal cannot push a 0/negative interval (which would hot-spin a
/// worker), an absurdly long interval (which would effectively disable a monitor) or an
/// out-of-range threshold. Watched service / application pool names are syntax-checked
/// because they end up as Service Control Manager / IIS identifiers.
/// </summary>
public static partial class RemoteConfigValidator
{
    /// <summary>Upper bound for every interval/duration field (one day).</summary>
    public const int MaxIntervalSeconds = 86400;

    /// <summary>Upper bound for queue-length thresholds.</summary>
    public const double MaxQueueThreshold = 10000;

    /// <summary>Maximum number of entries in a watched services / application pools list.</summary>
    public const int MaxWatchedEntries = 200;

    /// <summary>Maximum length of a single watched service / application pool name.</summary>
    public const int MaxNameLength = 256;

    [GeneratedRegex(@"^[\w .\-$@()]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WatchedNamePattern();

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
        if (config.Disk?.WarningThresholdPercent is double warning &&
            config.Disk.CriticalThresholdPercent is double critical &&
            warning > critical)
        {
            error = "Disk.WarningThresholdPercent must be <= Disk.CriticalThresholdPercent";
            return false;
        }
        if (!QueueThreshold(config.Disk?.QueueLengthWarningThreshold, "Disk.QueueLengthWarningThreshold", ref error)) return false;

        if (!Interval(config.Network?.PollIntervalSeconds, "Network.PollIntervalSeconds", ref error)) return false;
        if (!QueueThreshold(config.Network?.OutputQueueWarningThreshold, "Network.OutputQueueWarningThreshold", ref error)) return false;

        if (!Interval(config.Services?.PollIntervalSeconds, "Services.PollIntervalSeconds", ref error)) return false;
        if (!NameList(config.Services?.WatchedServices, "Services.WatchedServices", ref error)) return false;

        if (!Interval(config.AppPools?.PollIntervalSeconds, "AppPools.PollIntervalSeconds", ref error)) return false;
        if (!NameList(config.AppPools?.WatchedApplicationPools, "AppPools.WatchedApplicationPools", ref error)) return false;

        // Portal timing — these feed worker Task.Delay loops directly; a 0 would hot-spin.
        if (!Interval(config.Portal?.OutboxFlushIntervalSeconds, "Portal.OutboxFlushIntervalSeconds", ref error)) return false;
        if (!Interval(config.Portal?.CommandPollIntervalSeconds, "Portal.CommandPollIntervalSeconds", ref error)) return false;
        if (!Interval(config.Portal?.RemoteConfigPollIntervalSeconds, "Portal.RemoteConfigPollIntervalSeconds", ref error)) return false;

        return true;
    }

    private static bool Interval(int? value, string name, ref string error)
    {
        if (value.HasValue && (value.Value < 1 || value.Value > MaxIntervalSeconds))
        {
            error = $"{name} must be between 1 and {MaxIntervalSeconds}";
            return false;
        }
        return true;
    }

    private static bool Percent(double? value, string name, ref string error)
    {
        if (value.HasValue && !(value.Value >= 1 && value.Value <= 100))
        {
            error = $"{name} must be between 1 and 100";
            return false;
        }
        return true;
    }

    private static bool QueueThreshold(double? value, string name, ref string error)
    {
        // Written as a negated in-range test so NaN is rejected too.
        if (value.HasValue && !(value.Value >= 0 && value.Value <= MaxQueueThreshold))
        {
            error = $"{name} must be between 0 and {MaxQueueThreshold}";
            return false;
        }
        return true;
    }

    private static bool NameList(List<string>? names, string name, ref string error)
    {
        if (names == null)
            return true;

        if (names.Count > MaxWatchedEntries)
        {
            error = $"{name} must not contain more than {MaxWatchedEntries} entries";
            return false;
        }

        for (var i = 0; i < names.Count; i++)
        {
            var entry = names[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                error = $"{name}[{i}] must not be empty";
                return false;
            }
            if (entry.Length > MaxNameLength)
            {
                error = $"{name}[{i}] must not exceed {MaxNameLength} characters";
                return false;
            }
            if (!WatchedNamePattern().IsMatch(entry))
            {
                error = $"{name}[{i}] contains invalid characters";
                return false;
            }
        }
        return true;
    }
}