using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Google.Protobuf;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Contracts.Protobuf;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Tests.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// The <c>isStale</c> rule of the spec (SnapshotIsStale), on the wire: a batch built by the agent's
/// real outbox loop marks every snapshot entry older than its freshness window <c>isStale: true</c>
/// and every other one <c>false</c>, over HTTPS (validated strictly against the spec) and gRPC (a
/// real HTTP/2 call, decoded with the published proto), keeping each entry's last reading.
/// </summary>
public sealed class SnapshotStalenessContractTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_stale_contract_{Guid.NewGuid()}.db");
    private readonly DateTime _now = DateTime.UtcNow;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + ext); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>Entry name to expected isStale, with the default intervals plus the checks below.</summary>
    private static readonly Dictionary<string, bool> Expected = new()
    {
        [@"C:\"] = false,
        [@"D:\"] = true,             // 100 days: a not-ready drive that survived every purge
        ["0 C:"] = false,
        ["1 D:"] = true,             // 10 min 30 s > max(3 x 60 s, 10 min)
        ["Ethernet"] = false,
        ["USB Ethernet"] = true,
        ["W3SVC"] = false,
        ["OldSvc"] = true,
        ["DefaultAppPool"] = false,
        ["LegacyPool"] = true,
        ["shop-home"] = false,
        ["daily-cert"] = false,      // 47 h old, but a daily check is fresh for 48 h + its timeout
        ["hourly-api"] = true,       // 2 h 1 min > 2 x 3600 s + 30 s
        ["sql-port"] = true,         // ConfigurationError: does not run, its last result is 20 min old
    };

    private static readonly EndpointMonitorOptions Checks = new()
    {
        Endpoints =
        [
            new EndpointCheckOptions { Name = "shop-home", Target = "https://shop.example.com/" },
            new EndpointCheckOptions { Name = "daily-cert", Type = "Certificate", Target = "legacy.example.com", IntervalSeconds = 86_400, TimeoutSeconds = 20 },
            new EndpointCheckOptions { Name = "hourly-api", Target = "https://api.example.com/health", IntervalSeconds = 3600, TimeoutSeconds = 30 },
            new EndpointCheckOptions { Name = "sql-port", Type = "Tcp", Target = "sql01.corp.local" }, // invalid: no port
        ],
    };

    private MetricsStore Store()
    {
        DateTime Ago(TimeSpan span) => _now - span;
        var fresh = TimeSpan.FromMinutes(1);
        var past = TimeSpan.FromMinutes(10.5);
        var store = new MetricsStore();
        store.AddCpuMetric(new CpuMetric(Ago(TimeSpan.FromSeconds(5)), 12.5));
        store.AddDiskMetric(new DiskMetric(Ago(fresh), @"C:\", 126.4, 40.2, 68.2));
        store.AddDiskMetric(new DiskMetric(Ago(TimeSpan.FromDays(100)), @"D:\", 931.5, 210.7, 77.4));
        store.AddDiskQueueMetric(new DiskQueueMetric(Ago(fresh), "0 C:", 0.4));
        store.AddDiskQueueMetric(new DiskQueueMetric(Ago(past), "1 D:", 3));
        store.AddNetworkMetric(new NetworkMetric(Ago(fresh), "Ethernet", 0));
        store.AddNetworkMetric(new NetworkMetric(Ago(past), "USB Ethernet", 0));
        store.AddServiceStatus(new ServiceStatus(Ago(fresh), "W3SVC", "World Wide Web Publishing Service", "Running", true));
        store.AddServiceStatus(new ServiceStatus(Ago(past), "OldSvc", "Old service", "Stopped", false));
        store.AddApplicationPoolStatus(new ApplicationPoolStatus(Ago(fresh), "DefaultAppPool", "Started", true));
        store.AddApplicationPoolStatus(new ApplicationPoolStatus(Ago(past), "LegacyPool", "Started", true));
        store.AddEndpointStatus(new EndpointStatus(Ago(fresh), "shop-home", "Http", "https://shop.example.com/", "Up", true, 182, 61.4, "HTTP 200"));
        store.AddEndpointStatus(new EndpointStatus(Ago(TimeSpan.FromHours(47)), "daily-cert", "Certificate", "legacy.example.com:443", "Up", true, 40, 120.5, "Valid"));
        store.AddEndpointStatus(new EndpointStatus(Ago(TimeSpan.FromMinutes(121)), "hourly-api", "Http", "https://api.example.com/health", "Down", false, null, null, "HTTP 503"));
        store.AddEndpointStatus(new EndpointStatus(Ago(TimeSpan.FromMinutes(20)), "sql-port", "Tcp", "sql01.corp.local:1433",
            EndpointStatus.ConfigurationError, true, 3.1, null, "Configuration error: Monitoring:Endpoints:3:Target must be host:port. Last result: Up: Connected"));
        return store;
    }

    private static SnapshotFreshnessPolicy Policy() => new(
        new TestOptionsMonitor<DiskMonitorOptions>(new()),
        new TestOptionsMonitor<NetworkMonitorOptions>(new()),
        new TestOptionsMonitor<ServiceMonitorOptions>(new()),
        new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new()),
        new TestOptionsMonitor<EndpointMonitorOptions>(Checks));

    /// <summary>One collect + flush cycle of the agent's real outbox loop, sending through <paramref name="client"/>.</summary>
    private async Task RunOutboxCycleAsync(IPortalClient client, PortalOptions options)
    {
        using var outbox = new SqliteOutboxStore(_dbPath);
        var worker = new OutboxDispatchWorker(client, outbox, outbox, Store(), new MetricSnapshotTrigger(),
            new StaticOptionsMonitor<PortalOptions>(options), TimeProvider.System, NullLogger<OutboxDispatchWorker>.Instance,
            freshness: Policy());
        await worker.RunCycleAsync(Ct);
        (await outbox.GetPendingMessagesAsync(10)).Should().BeEmpty("the batch was delivered");
    }

    [Fact]
    public async Task Https_EntriesOlderThanTheirWindow_AreSentWithIsStaleTrue_TheOthersFalse()
    {
        var handler = new RecordingHandler();
        var options = AgentClientHarness.Options("https://portal.example.com");
        await RunOutboxCycleAsync(AgentClientHarness.CreateClient(options, () => handler), options);

        var body = handler.Requests.Should().ContainSingle().Subject.Body!;
        PortalSpec.Validate("MetricsBatch", body, SchemaMode.Strict).Should().BeEmpty(body);

        var batch = JsonNode.Parse(body)!.AsObject();
        var sent = new Dictionary<string, bool>();
        foreach (var (section, key) in new[]
                 {
                     ("diskMetrics", "driveName"), ("diskQueueMetrics", "instanceName"), ("networkMetrics", "interfaceName"),
                     ("serviceStatuses", "serviceName"), ("applicationPoolStatuses", "applicationPoolName"), ("endpointStatuses", "name"),
                 })
        {
            foreach (var entry in batch[section]!.AsArray())
            {
                entry!["isStale"].Should().NotBeNull($"every {section} entry carries isStale");
                sent[(string)entry[key]!] = (bool)entry["isStale"]!;
            }
        }
        sent.Should().BeEquivalentTo(Expected);

        // The last reading and its timestamp are kept as they were.
        var d = batch["diskMetrics"]!.AsArray().Single(e => (string)e!["driveName"]! == @"D:\")!;
        DateTime.Parse((string)d["timestamp"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .Should().Be(_now - TimeSpan.FromDays(100));
        ((double)d["freeGb"]!).Should().Be(210.7);
        var sql = batch["endpointStatuses"]!.AsArray().Single(e => (string)e!["name"]! == "sql-port")!;
        ((string)sql["status"]!).Should().Be(EndpointStatus.ConfigurationError, "a ConfigurationError entry keeps its status");
    }

    [Fact]
    public async Task Grpc_EntriesOlderThanTheirWindow_AreSentWithIsStaleTrue_TheOthersFalse()
    {
        await using var server = await GrpcCaptureServer.StartAsync();
        var options = AgentClientHarness.Options(server.BaseUrl);
        options.Protocol = "Grpc";
        using var client = new GrpcPortalClient(new StaticOptionsMonitor<PortalOptions>(options), Mock.Of<IHostEnvironment>(),
            NullLogger<GrpcPortalClient>.Instance);

        await RunOutboxCycleAsync(client, options);

        var payload = server.Received.Should().ContainSingle().Subject.Batch;
        var sent = new Dictionary<string, (bool Has, bool Value)>();
        foreach (var m in payload.DiskMetrics) sent[m.DriveName] = (m.HasIsStale, m.IsStale);
        foreach (var m in payload.DiskQueueMetrics) sent[m.InstanceName] = (m.HasIsStale, m.IsStale);
        foreach (var m in payload.NetworkMetrics) sent[m.InterfaceName] = (m.HasIsStale, m.IsStale);
        foreach (var m in payload.ServiceStatuses) sent[m.ServiceName] = (m.HasIsStale, m.IsStale);
        foreach (var m in payload.AppPoolStatuses) sent[m.AppPoolName] = (m.HasIsStale, m.IsStale);
        foreach (var m in payload.EndpointStatuses) sent[m.Name] = (m.HasIsStale, m.IsStale);

        sent.Values.Should().OnlyContain(v => v.Has, "is_stale is set on every snapshot entry, false included");
        sent.ToDictionary(e => e.Key, e => e.Value.Value).Should().BeEquivalentTo(Expected);
        payload.DiskMetrics.Single(m => m.DriveName == @"D:\").FreeGb.Should().Be(210.7);
        payload.EndpointStatuses.Single(m => m.Name == "sql-port").Status.Should().Be(EndpointStatus.ConfigurationError);
    }

    [Fact]
    public void Proto_IsStale_HasNewFieldNumbers_ThatNeverChange()
    {
        // Additive: each message gets is_stale under the next unused number. Renumbering (or
        // reusing a number) would break every portal compiled against the published proto.
        var numbers = new Dictionary<string, (int Number, int PreviousHighest)>
        {
            [nameof(DiskMetricProto)] = (DiskMetricProto.IsStaleFieldNumber, DiskMetricProto.UsagePercentFieldNumber),
            [nameof(DiskQueueMetricProto)] = (DiskQueueMetricProto.IsStaleFieldNumber, DiskQueueMetricProto.QueueLengthFieldNumber),
            [nameof(NetworkMetricProto)] = (NetworkMetricProto.IsStaleFieldNumber, NetworkMetricProto.OutputQueueLengthFieldNumber),
            [nameof(ServiceStatusProto)] = (ServiceStatusProto.IsStaleFieldNumber, ServiceStatusProto.IsRunningFieldNumber),
            [nameof(AppPoolStatusProto)] = (AppPoolStatusProto.IsStaleFieldNumber, AppPoolStatusProto.IsRunningFieldNumber),
            [nameof(EndpointStatusProto)] = (EndpointStatusProto.IsStaleFieldNumber, EndpointStatusProto.DetailFieldNumber),
        };

        numbers.ToDictionary(n => n.Key, n => n.Value.Number).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(DiskMetricProto)] = 6,
            [nameof(DiskQueueMetricProto)] = 4,
            [nameof(NetworkMetricProto)] = 4,
            [nameof(ServiceStatusProto)] = 6,
            [nameof(AppPoolStatusProto)] = 5,
            [nameof(EndpointStatusProto)] = 10,
        });
        numbers.Values.Should().OnlyContain(n => n.Number == n.PreviousHighest + 1);

        // Explicit presence (optional bool), so "unset" can mean unknown rather than false.
        foreach (var descriptor in new[]
                 {
                     DiskMetricProto.Descriptor, DiskQueueMetricProto.Descriptor, NetworkMetricProto.Descriptor,
                     ServiceStatusProto.Descriptor, AppPoolStatusProto.Descriptor, EndpointStatusProto.Descriptor,
                 })
        {
            var field = descriptor.FindFieldByName("is_stale");
            field.Should().NotBeNull(descriptor.Name);
            field.HasPresence.Should().BeTrue($"{descriptor.Name}.is_stale must be optional");
            descriptor.Fields.InFieldNumberOrder().Max(f => f.FieldNumber).Should().Be(field.FieldNumber, "it is the newest field");
        }
    }

    [Theory]
    [InlineData("DiskMetric")]
    [InlineData("DiskQueueMetric")]
    [InlineData("NetworkMetric")]
    [InlineData("ServiceStatus")]
    [InlineData("ApplicationPoolStatus")]
    [InlineData("EndpointStatus")]
    public void EverySnapshotSchema_DocumentsIsStale_AsOptionalAndAdditive(string schema)
    {
        var spec = PortalSpec.Schemas[schema]!.AsObject();
        var property = spec["properties"]!["isStale"]!.AsObject();

        ((string)property["$ref"]!).Should().Be("#/components/schemas/SnapshotIsStale");
        ((string)property["x-since"]!).Should().Be("1.0.20");
        (spec["required"]?.AsArray().Select(r => (string)r!) ?? []).Should().NotContain("isStale", "older agents don't send it");
        ((string)property["description"]!).Should().Contain("10 min").And.Contain("unknown");
    }

    [Fact]
    public void TheSharedDefinition_StatesTheRuleTheWindowsAndTheFallback()
    {
        var shared = PortalSpec.Schemas["SnapshotIsStale"]!.AsObject();
        ((string)shared["type"]!).Should().Be("boolean");
        var text = string.Join(' ', ((string)shared["description"]!).Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

        text.Should().Contain("batchedAt - timestamp > window")
            .And.Contain("max(3 x Monitoring:Disk:PollIntervalSeconds, 10 min)")
            .And.Contain("max(3 x Monitoring:Network:PollIntervalSeconds, 10 min)")
            .And.Contain("max(3 x Monitoring:Services:PollIntervalSeconds, 10 min)")
            .And.Contain("max(3 x Monitoring:ApplicationPools:PollIntervalSeconds, 10 min)")
            .And.Contain("max(2 x IntervalSeconds + TimeoutSeconds, 10 min)")
            .And.Contain("ConfigurationError")
            .And.Contain("UNKNOWN, not fresh")
            .And.Contain("MUST then fall back to the entry's `timestamp`");
        // The numbers in the text are the agent's.
        SnapshotFreshness.MinimumWindow.Should().Be(TimeSpan.FromMinutes(10));
        SnapshotFreshness.PolledIntervals.Should().Be(3);
        SnapshotFreshness.EndpointIntervals.Should().Be(2);
    }

    [Fact]
    public void TheSpecExample_MarksExactlyTheEntriesOlderThanTheirDefaultWindow()
    {
        // The example uses the default intervals, so every window is 10 minutes.
        var batch = PortalSpec.Pointer("#/components/examples/MetricsBatchFull/value").AsObject();
        var batchedAt = DateTimeOffset.Parse((string)batch["batchedAt"]!);
        var entries = new[] { "diskMetrics", "diskQueueMetrics", "networkMetrics", "serviceStatuses", "applicationPoolStatuses", "endpointStatuses" }
            .SelectMany(s => batch[s]!.AsArray()).ToList();

        entries.Should().OnlyContain(e => e!["isStale"] != null, "a current agent sends isStale on every snapshot entry");
        entries.Should().Contain(e => (bool)e!["isStale"]!, "the example shows a stale entry");
        foreach (var entry in entries)
        {
            var age = batchedAt - DateTimeOffset.Parse((string)entry!["timestamp"]!);
            ((bool)entry["isStale"]!).Should().Be(age > TimeSpan.FromMinutes(10), entry.ToJsonString());
        }
    }

    [Fact]
    public void ABatchWithoutIsStale_StaysWithoutIt_OnBothTransports()
    {
        // A batch an older agent queued before the update (outbox JSON without isStale) is
        // delivered as it was: isStale absent (JSON) / unset (gRPC) means "unknown", never false.
        const string queued = """
            {"agentId":"SRV-WEB-01","batchedAt":"2026-11-03T10:15:30.0000000Z","cpuMetrics":[],"ramMetrics":[],
             "diskMetrics":[{"timestamp":"2026-11-01T06:12:00.0000000Z","driveName":"D:\\","totalGb":512,"freeGb":301.7,"usagePercent":41.1}],
             "diskQueueMetrics":[],"networkMetrics":[],"serviceStatuses":[],"applicationPoolStatuses":[],
             "endpointStatuses":[{"timestamp":"2026-11-03T09:58:41.0000000Z","name":"sql-port","type":"Tcp","target":"sql01:1433","status":"Up","isUp":true}]}
            """;
        var batch = JsonSerializer.Deserialize<MetricsBatchDto>(queued, AgentClientHarness.WireOptions)!;

        batch.DiskMetrics.Single().IsStale.Should().BeNull();
        AgentPayloadSchemaTests.Serialize(batch).Should().NotContain("isStale");
        GrpcPortalClient.ToProto(batch.DiskMetrics.Single()).HasIsStale.Should().BeFalse();
        GrpcPortalClient.ToProto(batch.EndpointStatuses!.Single()).HasIsStale.Should().BeFalse();
        GrpcPortalClient.ToProto(batch.DiskMetrics.Single() with { IsStale = false }).HasIsStale.Should().BeTrue("false is sent, not dropped");
    }

    /// <summary>
    /// A plain HTTP/2 (h2c) endpoint answering SendMetricsBatch like a gRPC portal: it records each
    /// request as decoded with the published proto and replies accepted = true.
    /// </summary>
    private sealed class GrpcCaptureServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private GrpcCaptureServer(WebApplication app) => _app = app;

        public List<MetricsBatchRequest> Received { get; } = [];

        public string BaseUrl => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        public static async Task<GrpcCaptureServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
            var app = builder.Build();
            var server = new GrpcCaptureServer(app);
            app.MapPost("/watchtower.portal.v1.WatchTowerPortalService/SendMetricsBatch", async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                var frame = body.ToArray(); // 1 byte compressed flag, 4 bytes big-endian length, message
                var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1, 4));
                lock (server.Received)
                    server.Received.Add(MetricsBatchRequest.Parser.ParseFrom(frame, 5, length));

                var reply = new MetricsBatchResponse { Accepted = true }.ToByteArray();
                var output = new byte[5 + reply.Length];
                BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(1, 4), reply.Length);
                reply.CopyTo(output, 5);
                context.Response.ContentType = "application/grpc";
                await context.Response.Body.WriteAsync(output, context.RequestAborted);
                context.Response.AppendTrailer("grpc-status", "0");
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}