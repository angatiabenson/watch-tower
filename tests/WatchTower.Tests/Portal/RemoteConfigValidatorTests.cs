using AwesomeAssertions;

using WatchTower.Portal.Actions;
using WatchTower.Portal.Contracts;

namespace WatchTower.Tests.Portal;

public class RemoteConfigValidatorTests
{
    [Fact]
    public void Validate_EmptyConfig_IsValid()
    {
        var ok = RemoteConfigValidator.Validate(new RemoteAgentConfigDto(), out var error);
        ok.Should().BeTrue();
        error.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValidConfig_IsValid()
    {
        var config = new RemoteAgentConfigDto
        {
            Cpu = new RemoteCpuConfig { PollIntervalSeconds = 5, ThresholdPercent = 95, SustainedSeconds = 300 },
            Portal = new RemotePortalConfig { OutboxFlushIntervalSeconds = 30 }
        };
        RemoteConfigValidator.Validate(config, out _).Should().BeTrue();
    }

    [Fact]
    public void Validate_ZeroPortalFlushInterval_IsRejected()
    {
        // Regression guard: a 0 interval would hot-spin the OutboxDispatchWorker (Task.Delay(0)).
        var config = new RemoteAgentConfigDto
        {
            Portal = new RemotePortalConfig { OutboxFlushIntervalSeconds = 0 }
        };
        RemoteConfigValidator.Validate(config, out var error).Should().BeFalse();
        error.Should().Contain("OutboxFlushIntervalSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveCpuInterval_IsRejected(int interval)
    {
        var config = new RemoteAgentConfigDto { Cpu = new RemoteCpuConfig { PollIntervalSeconds = interval } };
        RemoteConfigValidator.Validate(config, out var error).Should().BeFalse();
        error.Should().Contain("Cpu.PollIntervalSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_OutOfRangeThreshold_IsRejected(double threshold)
    {
        var config = new RemoteAgentConfigDto { Ram = new RemoteRamConfig { ThresholdPercent = threshold } };
        RemoteConfigValidator.Validate(config, out var error).Should().BeFalse();
        error.Should().Contain("Ram.ThresholdPercent");
    }

    [Fact]
    public void Validate_NegativeRemoteConfigPollInterval_IsRejected()
    {
        var config = new RemoteAgentConfigDto
        {
            Portal = new RemotePortalConfig { RemoteConfigPollIntervalSeconds = -1 }
        };
        RemoteConfigValidator.Validate(config, out var error).Should().BeFalse();
        error.Should().Contain("RemoteConfigPollIntervalSeconds");
    }

    [Theory]
    [InlineData(86401)]
    [InlineData(int.MaxValue)]
    public void Validate_IntervalAboveOneDay_IsRejected(int interval)
    {
        var config = new RemoteAgentConfigDto { Services = new RemoteServiceConfig { PollIntervalSeconds = interval } };
        RemoteConfigValidator.Validate(config, out var error).Should().BeFalse();
        error.Should().Contain("Services.PollIntervalSeconds");
    }

    [Fact]
    public void Validate_IntervalOfExactlyOneDay_IsAccepted()
    {
        var config = new RemoteAgentConfigDto { Cpu = new RemoteCpuConfig { SustainedSeconds = 86400 } };
        RemoteConfigValidator.Validate(config, out _).Should().BeTrue();
    }

    [Fact]
    public void Validate_DiskWarningAboveCritical_IsRejected()
    {
        var config = new RemoteAgentConfigDto
        {
            Disk = new RemoteDiskConfig { WarningThresholdPercent = 90, CriticalThresholdPercent = 80 }
        };
        RemoteConfigValidator.Validate(config, out var error).Should().BeFalse();
        error.Should().Contain("Disk.WarningThresholdPercent");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(double.NaN)]
    public void Validate_QueueThresholdOutOfRange_IsRejected(double threshold)
    {
        var disk = new RemoteAgentConfigDto { Disk = new RemoteDiskConfig { QueueLengthWarningThreshold = threshold } };
        var net = new RemoteAgentConfigDto { Network = new RemoteNetworkConfig { OutputQueueWarningThreshold = threshold } };

        RemoteConfigValidator.Validate(disk, out var diskError).Should().BeFalse();
        diskError.Should().Contain("Disk.QueueLengthWarningThreshold");
        RemoteConfigValidator.Validate(net, out var netError).Should().BeFalse();
        netError.Should().Contain("Network.OutputQueueWarningThreshold");
    }

    [Fact]
    public void Validate_ZeroNetworkQueueThreshold_IsAccepted()
    {
        // 0 is the shipped default ("any queued packet is a warning"), so it must stay valid.
        var config = new RemoteAgentConfigDto { Network = new RemoteNetworkConfig { OutputQueueWarningThreshold = 0 } };
        RemoteConfigValidator.Validate(config, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("svc;rm -rf")]
    [InlineData("pool\"name")]
    [InlineData("a/b")]
    public void Validate_InvalidWatchedName_IsRejected(string name)
    {
        var services = new RemoteAgentConfigDto
        {
            Services = new RemoteServiceConfig { WatchedServices = new List<string> { "W3SVC", name } }
        };
        var pools = new RemoteAgentConfigDto
        {
            AppPools = new RemoteAppPoolConfig { WatchedApplicationPools = new List<string> { name } }
        };

        RemoteConfigValidator.Validate(services, out var svcError).Should().BeFalse();
        svcError.Should().Contain("Services.WatchedServices[1]");
        RemoteConfigValidator.Validate(pools, out var poolError).Should().BeFalse();
        poolError.Should().Contain("AppPools.WatchedApplicationPools[0]");
    }

    [Fact]
    public void Validate_RealisticWatchedNames_AreAccepted()
    {
        var config = new RemoteAgentConfigDto
        {
            Services = new RemoteServiceConfig
            {
                WatchedServices = new List<string> { "W3SVC", "MSSQL$SQLEXPRESS", "My Service (x64)", "svc.agent-1", "a@b" }
            },
            AppPools = new RemoteAppPoolConfig { WatchedApplicationPools = new List<string> { "DefaultAppPool", ".NET v4.5" } }
        };
        RemoteConfigValidator.Validate(config, out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_TooLongOrTooManyWatchedNames_IsRejected()
    {
        var tooLong = new RemoteAgentConfigDto
        {
            Services = new RemoteServiceConfig { WatchedServices = new List<string> { new('a', 257) } }
        };
        var tooMany = new RemoteAgentConfigDto
        {
            AppPools = new RemoteAppPoolConfig
            {
                WatchedApplicationPools = Enumerable.Range(0, 201).Select(i => $"pool{i}").ToList()
            }
        };

        RemoteConfigValidator.Validate(tooLong, out var longError).Should().BeFalse();
        longError.Should().Contain("256");
        RemoteConfigValidator.Validate(tooMany, out var manyError).Should().BeFalse();
        manyError.Should().Contain("200");
    }
}