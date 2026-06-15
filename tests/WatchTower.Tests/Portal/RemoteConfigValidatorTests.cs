using FluentAssertions;
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
}
