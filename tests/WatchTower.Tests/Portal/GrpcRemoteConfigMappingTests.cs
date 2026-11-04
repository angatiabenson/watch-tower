using AwesomeAssertions;

using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts.Protobuf;

namespace WatchTower.Tests.Portal;

public class GrpcRemoteConfigMappingTests
{
    [Fact]
    public void MapRemoteConfig_MapsEverySection()
    {
        var proto = new RemoteAgentConfig
        {
            Cpu = new RemoteCpuConfig { PollIntervalSeconds = 5, ThresholdPercent = 95, SustainedSeconds = 120 },
            Ram = new RemoteRamConfig { PollIntervalSeconds = 6, ThresholdPercent = 90, SustainedSeconds = 60 },
            Disk = new RemoteDiskConfig
            {
                PollIntervalSeconds = 60,
                WarningThresholdPercent = 70,
                CriticalThresholdPercent = 85,
                QueueLengthWarningThreshold = 2.5,
            },
            Network = new RemoteNetworkConfig { PollIntervalSeconds = 30, OutputQueueWarningThreshold = 4 },
            Services = new RemoteServiceConfig { PollIntervalSeconds = 15 },
            AppPools = new RemoteAppPoolConfig { PollIntervalSeconds = 20 },
            Portal = new RemotePortalConfig
            {
                OutboxFlushIntervalSeconds = 10,
                CommandPollIntervalSeconds = 11,
                RemoteConfigPollIntervalSeconds = 12,
            },
        };
        proto.Services.WatchedServices.Add("W3SVC");
        proto.AppPools.WatchedApplicationPools.Add("DefaultAppPool");

        var dto = GrpcPortalClient.MapRemoteConfig(proto);

        dto.Cpu!.SustainedSeconds.Should().Be(120);
        dto.Ram!.PollIntervalSeconds.Should().Be(6);
        dto.Disk.Should().NotBeNull();
        dto.Disk!.PollIntervalSeconds.Should().Be(60);
        dto.Disk.WarningThresholdPercent.Should().Be(70);
        dto.Disk.CriticalThresholdPercent.Should().Be(85);
        dto.Disk.QueueLengthWarningThreshold.Should().Be(2.5);
        dto.Network.Should().NotBeNull();
        dto.Network!.PollIntervalSeconds.Should().Be(30);
        dto.Network.OutputQueueWarningThreshold.Should().Be(4);
        dto.Services!.PollIntervalSeconds.Should().Be(15);
        dto.Services.WatchedServices.Should().Equal("W3SVC");
        dto.AppPools!.WatchedApplicationPools.Should().Equal("DefaultAppPool");
        dto.Portal.Should().NotBeNull();
        dto.Portal!.OutboxFlushIntervalSeconds.Should().Be(10);
        dto.Portal.CommandPollIntervalSeconds.Should().Be(11);
        dto.Portal.RemoteConfigPollIntervalSeconds.Should().Be(12);
    }

    [Fact]
    public void MapRemoteConfig_UnsetFieldsStayNull()
    {
        var dto = GrpcPortalClient.MapRemoteConfig(new RemoteAgentConfig { Cpu = new RemoteCpuConfig() });

        dto.Cpu.Should().NotBeNull();
        dto.Cpu!.PollIntervalSeconds.Should().BeNull();
        dto.Cpu.ThresholdPercent.Should().BeNull();
        dto.Disk.Should().BeNull();
        dto.Network.Should().BeNull();
        dto.Portal.Should().BeNull();
    }

    [Fact]
    public void MapRemoteConfig_ExplicitZeroIsPreserved_SoValidatorRejectsIt()
    {
        // Previously an explicit 0 was mapped to "unset" and silently ignored.
        var proto = new RemoteAgentConfig { Portal = new RemotePortalConfig { OutboxFlushIntervalSeconds = 0 } };

        var dto = GrpcPortalClient.MapRemoteConfig(proto);

        dto.Portal!.OutboxFlushIntervalSeconds.Should().Be(0);
        RemoteConfigValidator.Validate(dto, out var error).Should().BeFalse();
        error.Should().Contain("OutboxFlushIntervalSeconds");
    }

    [Fact]
    public void MapRemoteConfig_ExplicitZeroNetworkThresholdIsKept()
    {
        var proto = new RemoteAgentConfig { Network = new RemoteNetworkConfig { OutputQueueWarningThreshold = 0 } };

        GrpcPortalClient.MapRemoteConfig(proto).Network!.OutputQueueWarningThreshold.Should().Be(0);
    }

    [Fact]
    public void MapRemoteConfig_ExplicitValuesSurviveWireRoundTrip()
    {
        // Presence must survive serialization so a portal that sends an explicit 0 is honoured.
        var proto = new RemoteAgentConfig { Network = new RemoteNetworkConfig { OutputQueueWarningThreshold = 0 } };
        var parsed = RemoteAgentConfig.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(proto));

        GrpcPortalClient.MapRemoteConfig(parsed).Network!.OutputQueueWarningThreshold.Should().Be(0);
    }
}