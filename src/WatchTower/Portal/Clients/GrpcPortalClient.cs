using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Contracts.Protobuf;

namespace WatchTower.Portal.Clients;

/// <remarks>
/// <para>
/// Every call runs on one <see cref="Connection"/>: a snapshot of the VisibilityPortal settings
/// (BaseUrl, agent id, token and TLS settings) with the channel built from exactly that snapshot.
/// A call reads the current options once; if anything its connection was built from has changed,
/// a new connection is built for the new snapshot and swapped in under a lock (concurrent calls
/// agree on one), and the old one is disposed once the last call still running on it has finished.
/// The credentials (<c>authorization</c>, <c>x-agent-id</c>) are added by the connection's own
/// handler from its snapshot, and only to requests for the snapshot's BaseUrl origin; the
/// <c>AgentIdentity</c> in the message comes from the same snapshot. So one portal's token can never
/// travel to another portal's host, however the settings change while calls are running.
/// </para>
/// <para>
/// With <c>CompressRequests=Gzip</c> the metrics batch and event calls are sent with
/// <c>grpc-encoding: gzip</c> (gRPC's own message compression). A server that does not support the
/// encoding answers UNIMPLEMENTED naming it, before running the call: the call is resent once
/// uncompressed, on the same connection, and compression is switched off (see
/// <see cref="RequestCompression"/>).
/// </para>
/// </remarks>
public class GrpcPortalClient : IPortalClient, IDisposable
{
    private const int DeadlineSeconds = 30;

    /// <summary>Grpc.Net.Client's call header that selects the request message encoding.</summary>
    internal const string RequestEncodingHeader = "grpc-internal-encoding-request";

    /// <summary>
    /// How many times a compressible call takes a compression ticket and a connection before it
    /// gives up because the BaseUrl kept changing in between (it then fails, unsent, and is retried).
    /// </summary>
    internal const int MaxSnapshotAttempts = 2;

    private readonly IOptionsMonitor<PortalOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GrpcPortalClient> _logger;
    private readonly Func<PortalOptions, HttpMessageHandler>? _httpHandlerFactory;
    private readonly RequestCompression _compression;

    private readonly object _lock = new();
    private Connection? _current; // null after a failed rebuild, until the next call builds one
    private bool _disposed;

    public GrpcPortalClient(
        IOptionsMonitor<PortalOptions> options,
        IHostEnvironment environment,
        ILogger<GrpcPortalClient> logger)
        : this(options, environment, logger, httpHandlerFactory: null)
    {
    }

    /// <param name="httpHandlerFactory">
    /// Replaces the portal TLS handler (tests only): called with the snapshot each time a
    /// connection is built. The connection's channel disposes the handler it returns.
    /// </param>
    internal GrpcPortalClient(
        IOptionsMonitor<PortalOptions> options,
        IHostEnvironment environment,
        ILogger<GrpcPortalClient> logger,
        Func<PortalOptions, HttpMessageHandler>? httpHandlerFactory)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
        _httpHandlerFactory = httpHandlerFactory;

        // A configuration the agent can't connect with fails the start (as before); a later
        // change to one only fails the calls made under it (see Acquire).
        var opts = options.CurrentValue;
        _current = Build(opts, ConnectionKey.Of(opts));
        _compression = new RequestCompression(options, logger);
    }

    /// <summary>
    /// The settings a connection is built from. Any change starts a new connection: the BaseUrl,
    /// credentials and TLS settings are bound to the channel. (<c>Protocol</c> is read once, at
    /// startup, to choose the transport, and the remaining settings don't affect the channel.)
    /// </summary>
    private sealed record ConnectionKey(
        string BaseUrl,
        string AgentId,
        string AgentToken,
        bool RequireTls,
        bool AllowUntrustedCert,
        string? ClientCertThumbprint)
    {
        public static ConnectionKey Of(PortalOptions options) => new(
            options.BaseUrl ?? "",
            options.AgentId ?? "",
            options.AgentToken ?? "",
            options.Tls?.RequireTls ?? true,
            options.Tls?.AllowUntrustedCert ?? false,
            options.Tls?.ClientCertThumbprint);

        // Never print the token.
        public override string ToString() => BaseUrl;
    }

    /// <summary>One snapshot of the settings and the channel built from it.</summary>
    private sealed class Connection(ConnectionKey key, GrpcChannel channel)
    {
        public ConnectionKey Key { get; } = key;

        public GrpcChannel Channel { get; } = channel;

        public WatchTowerPortalService.WatchTowerPortalServiceClient Client { get; } = new(channel);

        /// <summary>Calls running on this connection (guarded by the client's lock).</summary>
        public int Leases;

        /// <summary>Replaced (or the client disposed): disposed when <see cref="Leases"/> drops to 0.</summary>
        public bool Retired;

        public AgentIdentity Identity() => new() { AgentId = Key.AgentId, AgentToken = Key.AgentToken };
    }

    /// <summary>
    /// A call could not be sent: the settings it would be sent under can't be connected with, or
    /// they changed while it was being prepared. Like a failed RPC, the call counts as not delivered.
    /// </summary>
    private sealed class PortalConnectionException(string message, Exception? inner = null)
        : Exception(message, inner);

    /// <summary>
    /// Adds the snapshot's credentials to every request of its channel, after checking that the
    /// request goes to the snapshot's BaseUrl origin (scheme, host and port): the gRPC counterpart
    /// of <see cref="AgentAuthHandler"/>, with the credentials fixed to the channel they belong to.
    /// </summary>
    private sealed class CredentialHandler(ConnectionKey key, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!IsForOrigin(request.RequestUri, key.BaseUrl))
            {
                throw new InvalidOperationException(
                    $"Refusing to send the agent credentials to '{request.RequestUri?.GetLeftPart(UriPartial.Authority)}': " +
                    $"they belong to {PortalOptions.SectionName}:BaseUrl '{key.BaseUrl}'.");
            }

            request.Headers.Remove("authorization");
            request.Headers.Remove("x-agent-id");
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {key.AgentToken}");
            request.Headers.TryAddWithoutValidation("x-agent-id", key.AgentId);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Whether <paramref name="requestUri"/> has the scheme, host and port of
    /// <paramref name="baseUrl"/>. gRPC method paths are absolute, so only the origin is compared.
    /// </summary>
    internal static bool IsForOrigin(Uri? requestUri, string baseUrl) =>
        Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var root) &&
        AgentAuthHandler.IsBelowBaseUrl(requestUri, root.GetLeftPart(UriPartial.Authority));

    /// <summary>
    /// Builds a connection for one snapshot. Throws when the snapshot can't be connected with
    /// (plain HTTP under RequireTls, AllowUntrustedCert in Production, a missing client certificate,
    /// an invalid BaseUrl).
    /// </summary>
    private Connection Build(PortalOptions opts, ConnectionKey key)
    {
        // Same transport policy as the HTTPS client: with RequireTls the BaseUrl must be https,
        // otherwise the bearer token and remote commands would travel in plaintext (h2c).
        PortalTls.EnsureTransportAllowed(opts);

        if (key.AllowUntrustedCert)
        {
            // Disabling TLS verification is a development convenience that is unsafe in
            // production (enables MITM injection of remote commands). Refuse to connect.
            if (_environment.IsProduction())
                throw new InvalidOperationException(
                    "VisibilityPortal.Tls.AllowUntrustedCert must not be enabled in Production.");

            _logger.LogWarning(
                "TLS certificate validation is DISABLED for the portal connection (AllowUntrustedCert=true). " +
                "This is unsafe and must only be used in development.");
        }

        // Throws a descriptive InvalidOperationException if ClientCertThumbprint is set but the
        // certificate cannot be loaded, rather than silently connecting without mutual TLS.
        HttpMessageHandler handler;
        if (_httpHandlerFactory is not null)
            handler = _httpHandlerFactory(opts);
        else
        {
            var tlsHandler = PortalTls.CreateHandler(opts.Tls ?? new TlsOptions(), allowUntrustedServerCert: key.AllowUntrustedCert);
            if (tlsHandler.SslOptions.ClientCertificates is { Count: > 0 })
                _logger.LogInformation("Presenting client certificate {Thumbprint} to the portal (gRPC)",
                    PortalTls.NormalizeThumbprint(key.ClientCertThumbprint!));
            handler = tlsHandler;
        }

        var credentials = new CredentialHandler(key, handler);
        try
        {
            var channel = GrpcChannel.ForAddress(key.BaseUrl, new GrpcChannelOptions
            {
                HttpHandler = credentials,
                DisposeHttpClient = true,
                CompressionProviders = [new Grpc.Net.Compression.GzipCompressionProvider(RequestCompression.Level)],
            });
            return new Connection(key, channel);
        }
        catch
        {
            credentials.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes the connection for the current settings, building (and swapping in) a new one when
    /// they changed; the caller must <see cref="Release"/> it. The options are read once, inside
    /// the lock, so the connection returned always matches one snapshot.
    /// </summary>
    /// <exception cref="PortalConnectionException">The current settings can't be connected with.</exception>
    private Connection Acquire()
    {
        Connection? unused = null;
        try
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var opts = _options.CurrentValue;
                var key = ConnectionKey.Of(opts);
                if (_current is null || _current.Key != key)
                {
                    // The old connection never serves the new settings, even if the new ones fail.
                    if (_current is not null)
                        unused = RetireLocked(_current);
                    _current = null;
                    try
                    {
                        _current = Build(opts, key);
                    }
                    catch (Exception ex)
                    {
                        throw new PortalConnectionException(
                            $"cannot connect with the current {PortalOptions.SectionName} settings: {ex.Message}", ex);
                    }
                    _logger.LogInformation("Portal settings changed; gRPC calls now go to {BaseUrl}", key.BaseUrl);
                }
                _current.Leases++;
                return _current;
            }
        }
        finally
        {
            unused?.Channel.Dispose();
        }
    }

    private void Release(Connection connection)
    {
        lock (_lock)
        {
            connection.Leases--;
            if (!connection.Retired || connection.Leases > 0)
                return;
        }
        connection.Channel.Dispose();
    }

    /// <summary>Marks a connection retired; returns it when no call uses it, for the caller to dispose (outside the lock).</summary>
    private static Connection? RetireLocked(Connection connection)
    {
        connection.Retired = true;
        return connection.Leases == 0 ? connection : null;
    }

    /// <summary>Runs a call on the connection for the current settings.</summary>
    private async Task<TReply> CallAsync<TReply>(Func<Connection, Task<TReply>> call)
    {
        var connection = Acquire();
        try
        {
            return await call(connection);
        }
        finally
        {
            Release(connection);
        }
    }

    /// <summary>
    /// Runs a compressible call: gzip while compression is on; when the server refuses the
    /// encoding, once more uncompressed (the refused call never ran, so nothing is duplicated).
    /// </summary>
    /// <remarks>
    /// The compression ticket is taken first, then the connection; both must be for the same
    /// BaseUrl, or the outcome would be applied to the wrong portal's compression state. If the
    /// BaseUrl changed in between, the call starts over with a new ticket and the new connection;
    /// if it changed again, the call fails unsent and the outbox retries it. The resend runs on the
    /// same connection (same host and credentials) as the refused attempt, and the outcome is
    /// applied to that ticket's generation only.
    /// </remarks>
    private async Task<TReply> CallCompressibleAsync<TReply>(Func<Connection, Metadata?, Task<TReply>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ticket = _compression.Begin();
            var connection = Acquire();
            try
            {
                if (!SameBaseUrl(ticket.BaseUrl, connection.Key.BaseUrl))
                {
                    if (attempt < MaxSnapshotAttempts)
                        continue;
                    throw new PortalConnectionException(
                        $"{PortalOptions.SectionName}:BaseUrl changed while the call was being prepared; not sent");
                }

                var gzip = ticket.Gzip;
                try
                {
                    var reply = await call(connection, gzip ? new Metadata { { RequestEncodingHeader, "gzip" } } : null);
                    if (gzip)
                        _compression.MarkAccepted(ticket);
                    return reply;
                }
                catch (RpcException ex) when (gzip && RefusesEncoding(ex))
                {
                    _compression.Disable(ticket, $"{ex.StatusCode} \"{ex.Status.Detail}\"");
                    return await call(connection, null);
                }
            }
            finally
            {
                Release(connection);
            }
        }
    }

    /// <summary>The same comparison as the compression state's own (trimmed, without trailing slashes).</summary>
    private static bool SameBaseUrl(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>gRPC servers answer an encoding they cannot read with UNIMPLEMENTED, naming the encoding.</summary>
    private static bool RefusesEncoding(RpcException ex) =>
        ex.StatusCode == StatusCode.Unimplemented &&
        ex.Status.Detail?.Contains("encoding", StringComparison.OrdinalIgnoreCase) == true;

    private static DateTime Deadline() => DateTime.UtcNow.AddSeconds(DeadlineSeconds);

    private static bool IsCallFailure(Exception ex) => ex is RpcException or PortalConnectionException;

    private void LogFailure(string call, Exception ex)
    {
        if (ex is RpcException rpc)
            _logger.LogWarning(ex, "gRPC {Call} failed: {Status}", call, rpc.Status);
        else
            _logger.LogWarning(ex, "gRPC {Call} not sent: {Reason}", call, ex.Message);
    }

    public async Task<bool> SendMetricsBatchAsync(MetricsBatchDto batch, CancellationToken ct)
    {
        try
        {
            var payload = new MetricsBatchPayload { BatchedAt = batch.BatchedAt, BatchId = batch.BatchId };
            if (batch.Agent is { } agent)
            {
                payload.Agent = new AgentTelemetryProto
                {
                    Version = agent.Version,
                    MachineName = agent.MachineName,
                    Os = agent.Os,
                    StartedAt = agent.StartedAt,
                    UptimeSeconds = agent.UptimeSeconds,
                    UpdatePhase = agent.UpdatePhase,
                };
                if (agent.AvailableVersion is not null)
                    payload.Agent.AvailableVersion = agent.AvailableVersion;
                if (agent.Maintenance is { } maintenance)
                {
                    payload.Agent.Maintenance = new MaintenanceProto { Active = maintenance.Active };
                    if (maintenance.UntilUtc is not null)
                        payload.Agent.Maintenance.UntilUtc = maintenance.UntilUtc;
                    if (maintenance.Reason is not null)
                        payload.Agent.Maintenance.Reason = maintenance.Reason;
                }
                if (agent.LastUpdateResult is { } result)
                    payload.Agent.LastUpdateResult = new UpdateResultProto
                    {
                        ToVersion = result.ToVersion,
                        Success = result.Success,
                        CompletedAt = result.CompletedAt,
                    };
            }
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
            payload.DiskMetrics.AddRange(batch.DiskMetrics.Select(ToProto));
            payload.DiskQueueMetrics.AddRange(batch.DiskQueueMetrics.Select(ToProto));
            payload.NetworkMetrics.AddRange(batch.NetworkMetrics.Select(ToProto));
            payload.ServiceStatuses.AddRange(batch.ServiceStatuses.Select(ToProto));
            payload.AppPoolStatuses.AddRange(batch.ApplicationPoolStatuses.Select(ToProto));

            if (batch.EndpointStatuses is { } endpoints)
                payload.EndpointStatuses.AddRange(endpoints.Select(ToProto));
            if (batch.PendingAlerts is { } pending)
                payload.PendingAlerts.AddRange(pending.Select(ToProto));

            var reply = await CallCompressibleAsync((connection, metadata) => connection.Client.SendMetricsBatchAsync(
                new MetricsBatchRequest { Identity = connection.Identity(), Batch = payload },
                headers: metadata, deadline: Deadline(), cancellationToken: ct).ResponseAsync);
            return reply.Accepted;
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("SendMetricsBatch", ex);
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
            if (evt.AlertKey is not null)
                protoEvt.AlertKey = evt.AlertKey;
            if (evt.Resolved is { } resolved)
                protoEvt.Resolved = resolved;
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

            var reply = await CallCompressibleAsync((connection, metadata) => connection.Client.SendEventAsync(
                new AgentEventRequest { Identity = connection.Identity(), Event = protoEvt },
                headers: metadata, deadline: Deadline(), cancellationToken: ct).ResponseAsync);
            return reply.Accepted;
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("SendEvent", ex);
            return false;
        }
    }

    public async Task<IReadOnlyList<AgentCommandDto>> PollCommandsAsync(CancellationToken ct)
    {
        try
        {
            var reply = await CallAsync(connection => connection.Client.PollCommandsAsync(
                new PollCommandsRequest { Identity = connection.Identity() },
                deadline: Deadline(), cancellationToken: ct).ResponseAsync);

            return reply.Commands.Select(c => new AgentCommandDto(
                c.CommandId,
                c.ActionType,
                new Dictionary<string, string>(c.Parameters),
                string.IsNullOrEmpty(c.ScheduledAt) ? null : c.ScheduledAt,
                c.IssuedAt
            )).ToList();
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("PollCommands", ex);
            return Array.Empty<AgentCommandDto>();
        }
    }

    public async Task<bool> AcknowledgeCommandAsync(AcknowledgeCommandDto ack, CancellationToken ct)
    {
        try
        {
            var reply = await CallAsync(connection => connection.Client.AcknowledgeCommandAsync(
                new AckCommandRequest
                {
                    Identity = connection.Identity(),
                    CommandId = ack.CommandId,
                    AcknowledgedAt = ack.AcknowledgedAt,
                },
                deadline: Deadline(), cancellationToken: ct).ResponseAsync);
            return reply.Ok;
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("AcknowledgeCommand", ex);
            return false;
        }
    }

    public async Task<bool> ReportCommandResultAsync(CommandResultDto result, CancellationToken ct)
    {
        try
        {
            var reply = await CallAsync(connection => connection.Client.ReportCommandResultAsync(
                new CommandResultRequest
                {
                    Identity = connection.Identity(),
                    CommandId = result.CommandId,
                    Success = result.Success,
                    Message = result.Message,
                    ExecutedAt = result.ExecutedAt,
                },
                deadline: Deadline(), cancellationToken: ct).ResponseAsync);
            return reply.Ok;
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("ReportCommandResult", ex);
            return false;
        }
    }

    public async Task<(bool HasUpdate, RemoteAgentConfigDto? Config, string? Version)>
        FetchConfigurationAsync(string? currentVersion, CancellationToken ct)
    {
        try
        {
            var reply = await CallAsync(connection => connection.Client.FetchConfigAsync(
                new FetchConfigRequest
                {
                    Identity = connection.Identity(),
                    CurrentVersion = currentVersion ?? string.Empty,
                },
                deadline: Deadline(), cancellationToken: ct).ResponseAsync);

            if (!reply.HasUpdate)
                return (false, null, null);

            var dto = MapRemoteConfig(reply.Config);
            return (true, dto, reply.Version);
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("FetchConfig", ex);
            return (false, null, null);
        }
    }

    public async Task<bool> AcknowledgeConfigAsync(string version, CancellationToken ct)
    {
        try
        {
            var reply = await CallAsync(connection => connection.Client.AcknowledgeConfigAsync(
                new AckConfigRequest
                {
                    Identity = connection.Identity(),
                    Version = version,
                },
                deadline: Deadline(), cancellationToken: ct).ResponseAsync);
            return reply.Ok;
        }
        catch (Exception ex) when (IsCallFailure(ex))
        {
            LogFailure("AcknowledgeConfig", ex);
            return false;
        }
    }

    /// <summary>A breach inside its waiting period (not an alert); every field is always set.</summary>
    internal static PendingAlertProto ToProto(PendingAlertDto p) => new()
    {
        AlertKey = p.AlertKey,
        Severity = p.Severity,
        Title = p.Title,
        Since = p.Since,
        AlertAfterSeconds = p.AlertAfterSeconds,
        AlertsAt = p.AlertsAt,
    };

    internal static EndpointStatusProto ToProto(EndpointStatusDto e)
    {
        var proto = new EndpointStatusProto
        {
            Timestamp = e.Timestamp,
            Name = e.Name,
            Type = e.Type,
            Target = e.Target,
            Status = e.Status,
            IsUp = e.IsUp,
        };
        if (e.ResponseTimeMs is { } ms)
            proto.ResponseTimeMs = ms;
        if (e.CertificateDaysLeft is { } days)
            proto.CertificateDaysLeft = days;
        if (e.Detail is not null)
            proto.Detail = e.Detail;
        if (e.IsStale is { } stale)
            proto.IsStale = stale;
        return proto;
    }

    // Snapshot entries: is_stale is optional, so it is only set when the batch carries it (a
    // batch queued by an agent that predates it has none, and unset means "unknown").

    internal static DiskMetricProto ToProto(DiskMetricDto m)
    {
        var proto = new DiskMetricProto
        {
            Timestamp = m.Timestamp,
            DriveName = m.DriveName,
            TotalGb = m.TotalGb,
            FreeGb = m.FreeGb,
            UsagePercent = m.UsagePercent,
        };
        if (m.IsStale is { } stale)
            proto.IsStale = stale;
        return proto;
    }

    internal static DiskQueueMetricProto ToProto(DiskQueueMetricDto m)
    {
        var proto = new DiskQueueMetricProto { Timestamp = m.Timestamp, InstanceName = m.InstanceName, QueueLength = m.QueueLength };
        if (m.IsStale is { } stale)
            proto.IsStale = stale;
        return proto;
    }

    internal static NetworkMetricProto ToProto(NetworkMetricDto m)
    {
        var proto = new NetworkMetricProto { Timestamp = m.Timestamp, InterfaceName = m.InterfaceName, OutputQueueLength = m.OutputQueueLength };
        if (m.IsStale is { } stale)
            proto.IsStale = stale;
        return proto;
    }

    internal static ServiceStatusProto ToProto(ServiceStatusDto m)
    {
        var proto = new ServiceStatusProto
        {
            Timestamp = m.Timestamp,
            ServiceName = m.ServiceName,
            DisplayName = m.DisplayName,
            Status = m.Status,
            IsRunning = m.IsRunning,
        };
        if (m.IsStale is { } stale)
            proto.IsStale = stale;
        return proto;
    }

    internal static AppPoolStatusProto ToProto(ApplicationPoolStatusDto m)
    {
        var proto = new AppPoolStatusProto { Timestamp = m.Timestamp, AppPoolName = m.ApplicationPoolName, Status = m.Status, IsRunning = m.IsRunning };
        if (m.IsStale is { } stale)
            proto.IsStale = stale;
        return proto;
    }

    /// <summary>
    /// Maps every section of the protobuf config onto the transport-neutral DTO. Scalar fields
    /// are declared <c>optional</c> in the proto, so presence (not a zero value) decides whether a
    /// setting is sent: an explicit 0 reaches <see cref="Actions.RemoteConfigValidator"/> and is
    /// rejected there instead of being silently dropped. Repeated fields carry no presence on
    /// the wire, so an empty watched list is treated as "not specified".
    /// </summary>
    internal static RemoteAgentConfigDto MapRemoteConfig(RemoteAgentConfig proto)
    {
        return new RemoteAgentConfigDto
        {
            Cpu = proto.Cpu == null ? null : new Contracts.RemoteCpuConfig
            {
                PollIntervalSeconds = proto.Cpu.HasPollIntervalSeconds ? proto.Cpu.PollIntervalSeconds : null,
                ThresholdPercent = proto.Cpu.HasThresholdPercent ? proto.Cpu.ThresholdPercent : null,
                SustainedSeconds = proto.Cpu.HasSustainedSeconds ? proto.Cpu.SustainedSeconds : null,
            },
            Ram = proto.Ram == null ? null : new Contracts.RemoteRamConfig
            {
                PollIntervalSeconds = proto.Ram.HasPollIntervalSeconds ? proto.Ram.PollIntervalSeconds : null,
                ThresholdPercent = proto.Ram.HasThresholdPercent ? proto.Ram.ThresholdPercent : null,
                SustainedSeconds = proto.Ram.HasSustainedSeconds ? proto.Ram.SustainedSeconds : null,
            },
            Disk = proto.Disk == null ? null : new Contracts.RemoteDiskConfig
            {
                PollIntervalSeconds = proto.Disk.HasPollIntervalSeconds ? proto.Disk.PollIntervalSeconds : null,
                WarningThresholdPercent = proto.Disk.HasWarningThresholdPercent
                    ? proto.Disk.WarningThresholdPercent : null,
                CriticalThresholdPercent = proto.Disk.HasCriticalThresholdPercent
                    ? proto.Disk.CriticalThresholdPercent : null,
                QueueLengthWarningThreshold = proto.Disk.HasQueueLengthWarningThreshold
                    ? proto.Disk.QueueLengthWarningThreshold : null,
                SustainedSeconds = proto.Disk.HasSustainedSeconds ? proto.Disk.SustainedSeconds : null,
                QueueSustainedSeconds = proto.Disk.HasQueueSustainedSeconds ? proto.Disk.QueueSustainedSeconds : null,
            },
            Network = proto.Network == null ? null : new Contracts.RemoteNetworkConfig
            {
                PollIntervalSeconds = proto.Network.HasPollIntervalSeconds ? proto.Network.PollIntervalSeconds : null,
                OutputQueueWarningThreshold = proto.Network.HasOutputQueueWarningThreshold
                    ? proto.Network.OutputQueueWarningThreshold : null,
                SustainedSeconds = proto.Network.HasSustainedSeconds ? proto.Network.SustainedSeconds : null,
            },
            Services = proto.Services == null ? null : new Contracts.RemoteServiceConfig
            {
                PollIntervalSeconds = proto.Services.HasPollIntervalSeconds ? proto.Services.PollIntervalSeconds : null,
                WatchedServices = proto.Services.WatchedServices.Count > 0
                    ? proto.Services.WatchedServices.ToList() : null,
                SustainedSeconds = proto.Services.HasSustainedSeconds ? proto.Services.SustainedSeconds : null,
            },
            AppPools = proto.AppPools == null ? null : new Contracts.RemoteAppPoolConfig
            {
                PollIntervalSeconds = proto.AppPools.HasPollIntervalSeconds ? proto.AppPools.PollIntervalSeconds : null,
                WatchedApplicationPools = proto.AppPools.WatchedApplicationPools.Count > 0
                    ? proto.AppPools.WatchedApplicationPools.ToList() : null,
                SustainedSeconds = proto.AppPools.HasSustainedSeconds ? proto.AppPools.SustainedSeconds : null,
            },
            Portal = proto.Portal == null ? null : new Contracts.RemotePortalConfig
            {
                OutboxFlushIntervalSeconds = proto.Portal.HasOutboxFlushIntervalSeconds
                    ? proto.Portal.OutboxFlushIntervalSeconds : null,
                CommandPollIntervalSeconds = proto.Portal.HasCommandPollIntervalSeconds
                    ? proto.Portal.CommandPollIntervalSeconds : null,
                RemoteConfigPollIntervalSeconds = proto.Portal.HasRemoteConfigPollIntervalSeconds
                    ? proto.Portal.RemoteConfigPollIntervalSeconds : null,
            },
            Maintenance = proto.Maintenance == null ? null : new Contracts.RemoteMaintenanceConfig
            {
                UntilUtc = proto.Maintenance.HasUntilUtc ? proto.Maintenance.UntilUtc : null,
                Reason = proto.Maintenance.HasReason ? proto.Maintenance.Reason : null,
            },
            Endpoints = proto.Endpoints == null ? null : new Contracts.RemoteEndpointConfig
            {
                SustainedSeconds = proto.Endpoints.HasSustainedSeconds ? proto.Endpoints.SustainedSeconds : null,
            },
        };
    }

    public void Dispose()
    {
        Connection? unused = null;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Calls still running finish on their connection; the last one disposes it.
            if (_current is not null)
                unused = RetireLocked(_current);
            _current = null;
        }
        _compression.Dispose();
        unused?.Channel.Dispose();
        GC.SuppressFinalize(this);
    }
}