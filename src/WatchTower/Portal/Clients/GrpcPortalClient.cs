using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Contracts.Protobuf;

namespace WatchTower.Portal.Clients;

public class GrpcPortalClient : IPortalClient, IDisposable
{
    private const int DeadlineSeconds = 30;

    private readonly GrpcChannel _channel;
    private readonly WatchTowerPortalService.WatchTowerPortalServiceClient _client;
    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly ILogger<GrpcPortalClient> _logger;

    public GrpcPortalClient(
        IOptionsMonitor<PortalOptions> options,
        IHostEnvironment environment,
        ILogger<GrpcPortalClient> logger)
    {
        _options = options;
        _logger = logger;

        var opts = options.CurrentValue;
        var channelOptions = new GrpcChannelOptions();

        if (opts.Tls.AllowUntrustedCert)
        {
            // Disabling TLS verification is a development convenience that is unsafe in
            // production (enables MITM injection of remote commands). Refuse to start.
            if (environment.IsProduction())
                throw new InvalidOperationException(
                    "VisibilityPortal.Tls.AllowUntrustedCert must not be enabled in Production.");

            _logger.LogWarning(
                "TLS certificate validation is DISABLED for the portal connection (AllowUntrustedCert=true). " +
                "This is unsafe and must only be used in development.");

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            channelOptions.HttpHandler = handler;
        }

        _channel = GrpcChannel.ForAddress(opts.BaseUrl, channelOptions);
        _client = new WatchTowerPortalService.WatchTowerPortalServiceClient(_channel);
    }

    private static DateTime Deadline() => DateTime.UtcNow.AddSeconds(DeadlineSeconds);

    private Metadata BuildMetadata()
    {
        var opts = _options.CurrentValue;
        return new Metadata
        {
            { "x-agent-id", opts.AgentId },
            { "authorization", $"Bearer {opts.AgentToken}" },
        };
    }

    private AgentIdentity BuildIdentity()
    {
        var opts = _options.CurrentValue;
        return new AgentIdentity { AgentId = opts.AgentId, AgentToken = opts.AgentToken };
    }

    public async Task<bool> SendMetricsBatchAsync(MetricsBatchDto batch, CancellationToken ct)
    {
        try
        {
            var payload = new MetricsBatchPayload { BatchedAt = batch.BatchedAt, BatchId = batch.BatchId };
            payload.CpuMetrics.AddRange(
                batch.CpuMetrics.Select(m => new CpuMetricProto
                { Timestamp = m.Timestamp, UsagePercent = m.UsagePercent }));
            payload.RamMetrics.AddRange(
                batch.RamMetrics.Select(m => new RamMetricProto
                {
                    Timestamp = m.Timestamp,
                    UsagePercent = m.UsagePercent,
                    AvailableMb = m.AvailableMb,
                    TotalMb = m.TotalMb
                }));
            payload.DiskMetrics.AddRange(
                batch.DiskMetrics.Select(m => new DiskMetricProto
                {
                    Timestamp = m.Timestamp,
                    DriveName = m.DriveName,
                    TotalGb = m.TotalGb,
                    FreeGb = m.FreeGb,
                    UsagePercent = m.UsagePercent
                }));
            payload.DiskQueueMetrics.AddRange(
                batch.DiskQueueMetrics.Select(m => new DiskQueueMetricProto
                {
                    Timestamp = m.Timestamp,
                    InstanceName = m.InstanceName,
                    QueueLength = m.QueueLength
                }));
            payload.NetworkMetrics.AddRange(
                batch.NetworkMetrics.Select(m => new NetworkMetricProto
                {
                    Timestamp = m.Timestamp,
                    InterfaceName = m.InterfaceName,
                    OutputQueueLength = m.OutputQueueLength
                }));
            payload.ServiceStatuses.AddRange(
                batch.ServiceStatuses.Select(m => new ServiceStatusProto
                {
                    Timestamp = m.Timestamp,
                    ServiceName = m.ServiceName,
                    DisplayName = m.DisplayName,
                    Status = m.Status,
                    IsRunning = m.IsRunning
                }));
            payload.AppPoolStatuses.AddRange(
                batch.ApplicationPoolStatuses.Select(m => new AppPoolStatusProto
                {
                    Timestamp = m.Timestamp,
                    AppPoolName = m.ApplicationPoolName,
                    Status = m.Status,
                    IsRunning = m.IsRunning
                }));

            var req = new MetricsBatchRequest { Identity = BuildIdentity(), Batch = payload };
            var reply = await _client.SendMetricsBatchAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);
            return reply.Accepted;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC SendMetricsBatch failed: {Status}", ex.Status);
            return false;
        }
    }

    public async Task<bool> SendEventAsync(AgentEventDto evt, CancellationToken ct)
    {
        try
        {
            var protoEvt = new AgentEvent
            {
                EventId = evt.EventId,
                Timestamp = evt.Timestamp,
                IncidentType = evt.IncidentType,
                Description = evt.Description,
            };
            if (evt.TopProcesses != null)
                protoEvt.TopProcesses.AddRange(evt.TopProcesses.Select(p =>
                    new ProcessInfoProto
                    {
                        Name = p.Name,
                        Pid = p.Pid,
                        CpuSeconds = p.CpuSeconds,
                        MemoryMb = p.MemoryMb,
                        CpuPercent = p.CpuPercent
                    }));

            var req = new AgentEventRequest { Identity = BuildIdentity(), Event = protoEvt };
            var reply = await _client.SendEventAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);
            return reply.Accepted;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC SendEvent failed: {Status}", ex.Status);
            return false;
        }
    }

    public async Task<IReadOnlyList<AgentCommandDto>> PollCommandsAsync(CancellationToken ct)
    {
        try
        {
            var req = new PollCommandsRequest { Identity = BuildIdentity() };
            var reply = await _client.PollCommandsAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);

            return reply.Commands.Select(c => new AgentCommandDto(
                c.CommandId,
                c.ActionType,
                new Dictionary<string, string>(c.Parameters),
                string.IsNullOrEmpty(c.ScheduledAt) ? null : c.ScheduledAt,
                c.IssuedAt
            )).ToList();
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC PollCommands failed: {Status}", ex.Status);
            return Array.Empty<AgentCommandDto>();
        }
    }

    public async Task<bool> AcknowledgeCommandAsync(AcknowledgeCommandDto ack, CancellationToken ct)
    {
        try
        {
            var req = new AckCommandRequest
            {
                Identity = BuildIdentity(),
                CommandId = ack.CommandId,
                AcknowledgedAt = ack.AcknowledgedAt,
            };
            var reply = await _client.AcknowledgeCommandAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);
            return reply.Ok;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC AcknowledgeCommand failed: {Status}", ex.Status);
            return false;
        }
    }

    public async Task<bool> ReportCommandResultAsync(CommandResultDto result, CancellationToken ct)
    {
        try
        {
            var req = new CommandResultRequest
            {
                Identity = BuildIdentity(),
                CommandId = result.CommandId,
                Success = result.Success,
                Message = result.Message,
                ExecutedAt = result.ExecutedAt,
            };
            var reply = await _client.ReportCommandResultAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);
            return reply.Ok;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC ReportCommandResult failed: {Status}", ex.Status);
            return false;
        }
    }

    public async Task<(bool HasUpdate, RemoteAgentConfigDto? Config, string? Version)>
        FetchConfigurationAsync(string? currentVersion, CancellationToken ct)
    {
        try
        {
            var req = new FetchConfigRequest
            {
                Identity = BuildIdentity(),
                CurrentVersion = currentVersion ?? string.Empty,
            };
            var reply = await _client.FetchConfigAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);

            if (!reply.HasUpdate)
                return (false, null, null);

            var dto = MapRemoteConfig(reply.Config);
            return (true, dto, reply.Version);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC FetchConfig failed: {Status}", ex.Status);
            return (false, null, null);
        }
    }

    public async Task<bool> AcknowledgeConfigAsync(string version, CancellationToken ct)
    {
        try
        {
            var req = new AckConfigRequest
            {
                Identity = BuildIdentity(),
                Version = version,
            };
            var reply = await _client.AcknowledgeConfigAsync(req, headers: BuildMetadata(), deadline: Deadline(),
                cancellationToken: ct);
            return reply.Ok;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC AcknowledgeConfig failed: {Status}", ex.Status);
            return false;
        }
    }

    private static RemoteAgentConfigDto MapRemoteConfig(RemoteAgentConfig proto)
    {
        return new RemoteAgentConfigDto
        {
            Cpu = proto.Cpu == null ? null : new Contracts.RemoteCpuConfig
            {
                PollIntervalSeconds = proto.Cpu.PollIntervalSeconds == 0 ? null : proto.Cpu.PollIntervalSeconds,
                ThresholdPercent = proto.Cpu.ThresholdPercent == 0 ? null : proto.Cpu.ThresholdPercent,
                SustainedSeconds = proto.Cpu.SustainedSeconds == 0 ? null : proto.Cpu.SustainedSeconds,
            },
            Ram = proto.Ram == null ? null : new Contracts.RemoteRamConfig
            {
                PollIntervalSeconds = proto.Ram.PollIntervalSeconds == 0 ? null : proto.Ram.PollIntervalSeconds,
                ThresholdPercent = proto.Ram.ThresholdPercent == 0 ? null : proto.Ram.ThresholdPercent,
                SustainedSeconds = proto.Ram.SustainedSeconds == 0 ? null : proto.Ram.SustainedSeconds,
            },
            Services = proto.Services == null ? null : new Contracts.RemoteServiceConfig
            {
                PollIntervalSeconds = proto.Services.PollIntervalSeconds == 0 ? null : proto.Services.PollIntervalSeconds,
                WatchedServices = proto.Services.WatchedServices.Count > 0
                    ? proto.Services.WatchedServices.ToList() : null,
            },
            AppPools = proto.AppPools == null ? null : new Contracts.RemoteAppPoolConfig
            {
                PollIntervalSeconds = proto.AppPools.PollIntervalSeconds == 0 ? null : proto.AppPools.PollIntervalSeconds,
                WatchedApplicationPools = proto.AppPools.WatchedApplicationPools.Count > 0
                    ? proto.AppPools.WatchedApplicationPools.ToList() : null,
            },
        };
    }

    public void Dispose() => _channel.Dispose();
}