using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Google.Protobuf;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.PortalContract;
using WatchTower.Workers;

using Proto = WatchTower.Portal.Contracts.Protobuf;

namespace WatchTower.Tests.Portal;

/// <summary>
/// The portal side of waiting periods: pending breaches in every metrics batch (HTTPS and gRPC),
/// and the <c>...SustainedSeconds</c> settings through remote configuration (DTO, validator,
/// apply handler, gRPC mapping).
/// </summary>
public sealed class WaitingPeriodPortalTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_waiting_{Guid.NewGuid()}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + ext); } catch (IOException) { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ metrics batches

    private async Task<JsonObject> SendOneBatchAsync(PendingAlertTracker tracker)
    {
        var store = new MetricsStore();
        store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 12.5));
        var handler = new RecordingHandler();
        var options = AgentClientHarness.Options("https://portal.example.com");
        using var outbox = new SqliteOutboxStore(_dbPath);
        var worker = new OutboxDispatchWorker(AgentClientHarness.CreateClient(options, () => handler), outbox, outbox, store,
            new MetricSnapshotTrigger(), new StaticOptionsMonitor<PortalOptions>(options), TimeProvider.System,
            NullLogger<OutboxDispatchWorker>.Instance, pendingAlerts: tracker);

        await worker.RunCycleAsync(Ct);

        var body = handler.Requests.Should().ContainSingle().Subject.Body!;
        PortalSpec.Validate("MetricsBatch", body, SchemaMode.Strict).Should().BeEmpty(body);
        return JsonNode.Parse(body)!.AsObject();
    }

    [Fact]
    public async Task Batch_CarriesPendingBreaches_ApartFromAlerts()
    {
        var tracker = new PendingAlertTracker();
        var since = DateTime.UtcNow.AddMinutes(-2);
        tracker.Observe(AlertKey.DiskQueue("0 C:"), AlertSeverity.Warning, TimeSpan.FromMinutes(5), "Disk Queue High: 0 C: at 3.4",
            since, openSeverity: null);

        var batch = await SendOneBatchAsync(tracker);

        var pending = batch["pendingAlerts"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        ((string)pending["alertKey"]!).Should().Be("diskqueue:0 C:");
        ((string)pending["severity"]!).Should().Be("Warning");
        ((string)pending["title"]!).Should().Be("Disk Queue High: 0 C: at 3.4");
        ((int)pending["alertAfterSeconds"]!).Should().Be(300);
        DateTime.Parse((string)pending["since"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().Be(since);
        DateTime.Parse((string)pending["alertsAt"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .Should().Be(since.AddMinutes(5));
        ((string)pending["since"]!).Should().EndWith("Z");
    }

    [Fact]
    public async Task Batch_WithNothingPending_SendsAnEmptyList()
    {
        var batch = await SendOneBatchAsync(new PendingAlertTracker());

        batch["pendingAlerts"]!.AsArray().Should().BeEmpty("present and empty: nothing is waiting");
    }

    [Fact]
    public void Grpc_PendingAlert_MapsEveryField_AndSurvivesTheWire()
    {
        var dto = new PendingAlertDto("svc:Spooler", "Critical", "Service Down: Spooler",
            "2026-11-03T10:00:00.0000000Z", 120, "2026-11-03T10:02:00.0000000Z");
        var payload = new Proto.MetricsBatchPayload();
        payload.PendingAlerts.Add(GrpcPortalClient.ToProto(dto));

        var parsed = Proto.MetricsBatchPayload.Parser.ParseFrom(payload.ToByteArray()).PendingAlerts.Single();

        parsed.AlertKey.Should().Be("svc:Spooler");
        parsed.Severity.Should().Be("Critical");
        parsed.Title.Should().Be("Service Down: Spooler");
        parsed.Since.Should().Be("2026-11-03T10:00:00.0000000Z");
        parsed.AlertAfterSeconds.Should().Be(120);
        parsed.AlertsAt.Should().Be("2026-11-03T10:02:00.0000000Z");
        Proto.MetricsBatchPayload.PendingAlertsFieldNumber.Should().Be(12, "field numbers never change");
    }

    // ------------------------------------------------------------------ remote configuration

    private const string WaitingPeriodsJson = """
        {
          "cpu": { "sustainedSeconds": 0 },
          "ram": { "sustainedSeconds": 240 },
          "disk": { "sustainedSeconds": 900, "queueSustainedSeconds": 420 },
          "network": { "sustainedSeconds": 360 },
          "services": { "sustainedSeconds": 180 },
          "appPools": { "sustainedSeconds": 0 },
          "endpoints": { "sustainedSeconds": 150 }
        }
        """;

    [Fact]
    public void Dto_ReadsTheWaitingPeriods_FromTheWireNames()
    {
        var config = JsonSerializer.Deserialize<RemoteAgentConfigDto>(WaitingPeriodsJson, AgentClientHarness.WireOptions)!;

        config.Cpu!.SustainedSeconds.Should().Be(0);
        config.Ram!.SustainedSeconds.Should().Be(240);
        config.Disk!.SustainedSeconds.Should().Be(900);
        config.Disk.QueueSustainedSeconds.Should().Be(420);
        config.Network!.SustainedSeconds.Should().Be(360);
        config.Services!.SustainedSeconds.Should().Be(180);
        config.AppPools!.SustainedSeconds.Should().Be(0);
        config.Endpoints!.SustainedSeconds.Should().Be(150);
        RemoteConfigValidator.Validate(config, out var error).Should().BeTrue(error);
    }

    public static TheoryData<string, Action<RemoteAgentConfigDto, int>> WaitingFields() => new()
    {
        { "Cpu.SustainedSeconds", (c, v) => c.Cpu = new RemoteCpuConfig { SustainedSeconds = v } },
        { "Ram.SustainedSeconds", (c, v) => c.Ram = new RemoteRamConfig { SustainedSeconds = v } },
        { "Disk.SustainedSeconds", (c, v) => c.Disk = new RemoteDiskConfig { SustainedSeconds = v } },
        { "Disk.QueueSustainedSeconds", (c, v) => c.Disk = new RemoteDiskConfig { QueueSustainedSeconds = v } },
        { "Network.SustainedSeconds", (c, v) => c.Network = new RemoteNetworkConfig { SustainedSeconds = v } },
        { "Services.SustainedSeconds", (c, v) => c.Services = new RemoteServiceConfig { SustainedSeconds = v } },
        { "AppPools.SustainedSeconds", (c, v) => c.AppPools = new RemoteAppPoolConfig { SustainedSeconds = v } },
        { "Endpoints.SustainedSeconds", (c, v) => c.Endpoints = new RemoteEndpointConfig { SustainedSeconds = v } },
    };

    [Theory]
    [MemberData(nameof(WaitingFields))]
    public void Validator_AcceptsZeroToOneDay_AndRejectsTheRest(string name, Action<RemoteAgentConfigDto, int> set)
    {
        foreach (var ok in new[] { 0, 1, 300, 86_400 })
        {
            var config = new RemoteAgentConfigDto();
            set(config, ok);
            RemoteConfigValidator.Validate(config, out var error).Should().BeTrue($"{name} = {ok}: {error}");
        }

        foreach (var bad in new[] { -1, 86_401, int.MaxValue, int.MinValue })
        {
            var config = new RemoteAgentConfigDto();
            set(config, bad);
            RemoteConfigValidator.Validate(config, out var error).Should().BeFalse($"{name} = {bad}");
            error.Should().Be($"{name} must be between 0 and 86400");
        }
    }

    [Fact]
    public async Task ApplyHandler_WritesTheWaitingPeriods_WhereTheMonitorsReadThem()
    {
        using var dir = new TestDataDirectory();
        var handler = new ApplyConfigurationHandler(Mock.Of<IRemoteConfigStore>(), dir.Paths,
            new TestOptionsMonitor<PortalOptions>(new PortalOptions { Enabled = true, RemoteConfig = { Enabled = true } }),
            NullLogger<ApplyConfigurationHandler>.Instance);

        var result = await handler.ExecuteAsync(new Dictionary<string, string> { ["ConfigJson"] = WaitingPeriodsJson, ["Version"] = "v1" }, Ct);

        result.Success.Should().BeTrue(result.Message);
        var config = new ConfigurationBuilder().AddJsonFile(dir.Paths.RemoteConfigFilePath).Build();
        T Bind<T>(string section) where T : new() => config.GetSection(section).Get<T>() ?? new T();
        Bind<CpuMonitorOptions>(CpuMonitorOptions.SectionName).SustainedSeconds.Should().Be(0);
        Bind<RamMonitorOptions>(RamMonitorOptions.SectionName).SustainedSeconds.Should().Be(240);
        var disk = Bind<DiskMonitorOptions>(DiskMonitorOptions.SectionName);
        disk.SustainedSeconds.Should().Be(900);
        disk.QueueSustainedSeconds.Should().Be(420);
        disk.CriticalThresholdPercent.Should().Be(85, "settings the portal did not send keep their defaults");
        Bind<NetworkMonitorOptions>(NetworkMonitorOptions.SectionName).SustainedSeconds.Should().Be(360);
        Bind<ServiceMonitorOptions>(ServiceMonitorOptions.SectionName).SustainedSeconds.Should().Be(180);
        Bind<ApplicationPoolMonitorOptions>(ApplicationPoolMonitorOptions.SectionName).SustainedSeconds.Should().Be(0);
        var endpoints = Bind<EndpointMonitorOptions>(EndpointMonitorOptions.SectionName);
        endpoints.EndpointDefaults.SustainedSeconds.Should().Be(150);
        endpoints.Endpoints.Should().BeEmpty("the portal sets the default only, never the checks");
    }

    [Fact]
    public void ApplyHandler_LeavesOutWaitingPeriodsThePortalDidNotSend()
    {
        var json = ApplyConfigurationHandler.BuildConfigFileJson(new RemoteAgentConfigDto
        {
            Disk = new RemoteDiskConfig { CriticalThresholdPercent = 90 },
            Endpoints = new RemoteEndpointConfig(),
        });

        json.Should().NotContain("Sustained").And.NotContain("EndpointDefaults");
    }

    [Fact]
    public void Grpc_RemoteConfig_MapsTheWaitingPeriods_ZeroIncluded()
    {
        var proto = new Proto.RemoteAgentConfig
        {
            Cpu = new Proto.RemoteCpuConfig { SustainedSeconds = 0 },
            Disk = new Proto.RemoteDiskConfig { SustainedSeconds = 900, QueueSustainedSeconds = 420 },
            Network = new Proto.RemoteNetworkConfig { SustainedSeconds = 360 },
            Services = new Proto.RemoteServiceConfig { SustainedSeconds = 180 },
            AppPools = new Proto.RemoteAppPoolConfig { SustainedSeconds = 0 },
            Endpoints = new Proto.RemoteEndpointConfig { SustainedSeconds = 150 },
        };

        var dto = GrpcPortalClient.MapRemoteConfig(Proto.RemoteAgentConfig.Parser.ParseFrom(proto.ToByteArray()));

        dto.Cpu!.SustainedSeconds.Should().Be(0, "an explicit 0 is sent and kept");
        dto.Disk!.SustainedSeconds.Should().Be(900);
        dto.Disk.QueueSustainedSeconds.Should().Be(420);
        dto.Network!.SustainedSeconds.Should().Be(360);
        dto.Services!.SustainedSeconds.Should().Be(180);
        dto.AppPools!.SustainedSeconds.Should().Be(0);
        dto.Endpoints!.SustainedSeconds.Should().Be(150);
        RemoteConfigValidator.Validate(dto, out var error).Should().BeTrue(error);

        var unset = GrpcPortalClient.MapRemoteConfig(new Proto.RemoteAgentConfig
        {
            Disk = new Proto.RemoteDiskConfig(),
            Endpoints = new Proto.RemoteEndpointConfig(),
        });
        unset.Disk!.SustainedSeconds.Should().BeNull();
        unset.Disk.QueueSustainedSeconds.Should().BeNull();
        unset.Endpoints!.SustainedSeconds.Should().BeNull();
        unset.Network.Should().BeNull();
    }
}