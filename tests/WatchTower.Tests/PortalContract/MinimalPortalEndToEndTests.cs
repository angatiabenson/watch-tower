using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

using Sample = WatchTower.Samples.MinimalPortal;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// (c) The agent's real <see cref="HttpsPortalClient"/> (and its real outbox, command and remote-config
/// loops) against <c>samples/MinimalPortal</c> hosted in-process on a TestServer, at the site root and
/// below a path prefix.
/// </summary>
public sealed class MinimalPortalEndToEndTests : IAsyncLifetime
{
    private readonly List<WebApplication> _portals = new();
    private readonly List<string> _tempPaths = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var portal in _portals)
            await portal.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private sealed record Portal(WebApplication App, Sample.PortalState State, string AgentId, string Token, string BaseUrl)
    {
        public TestServer Server => App.GetTestServer();

        public Sample.AgentRecord Agent => State.Find(AgentId)!;

        public HttpsPortalClient Client(string? token = null, string? baseUrl = null) => AgentClientHarness.CreateClient(
            AgentClientHarness.Options(baseUrl ?? BaseUrl, AgentId, token ?? Token), () => Server.CreateHandler());

        public HttpClient Admin()
        {
            var http = Server.CreateClient();
            http.BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("X-Admin-Key", State.Options.AdminKey);
            return http;
        }
    }

    private async Task<Portal> StartPortalAsync(string pathBase)
    {
        string[] args = pathBase.Length == 0 ? [] : ["--PathBase", pathBase];
        var app = Sample.MinimalPortalApp.Build(args, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        _portals.Add(app);

        var state = app.Services.GetRequiredService<Sample.PortalState>();
        var agent = state.CreateAgent(AgentClientHarness.AgentId);
        // TestServer answers on http://localhost; the agent appends /api/v1/agent/... to BaseUrl.
        return new Portal(app, state, agent.AgentId, agent.Token, $"http://localhost{pathBase}/");
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempPaths.Add(path);
        return path;
    }

    // ── the client against the portal ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("/watchtower")]
    public async Task MetricsBatch_IsStored_AndARetryIsADuplicate(string pathBase)
    {
        var portal = await StartPortalAsync(pathBase);
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), ct)).Should().BeTrue();
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), ct)).Should().BeTrue("a retried batch is still answered 2xx");

        var agent = portal.Agent;
        agent.BatchesReceived.Should().Be(1);
        agent.DuplicateBatches.Should().Be(1);
        agent.Cpu.Should().HaveCount(2);
        agent.Ram.Should().ContainSingle();
        agent.Disks.Keys.Should().BeEquivalentTo(@"C:\", @"D:\");
        agent.Services["W3SVC"].IsRunning.Should().BeTrue();
        agent.AppPools.Should().ContainKey("DefaultAppPool");
        agent.Endpoints["old-cert"].CertificateDaysLeft.Should().Be(-2.5);
        agent.Telemetry!.Version.Should().Be("1.6.0");
        agent.Telemetry.Maintenance!.Active.Should().BeTrue();
        agent.Telemetry.LastUpdateResult!.CompletedAt.Offset.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task EndpointConfigurationError_ReplacesTheLastResult_AndALateOlderBatchDoesNotBringItBack()
    {
        var portal = await StartPortalAsync("");
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;

        // The agent's own marking of a check left invalid: same timestamp as its last result.
        var checkedAt = AgentPayloads.Now.AddMinutes(-5);
        var last = new EndpointStatus(checkedAt, "shop-home", "Http", "https://shop.example.com/", "Up", true, 182, null, "HTTP 200");
        var marked = EndpointMonitorWorker.MarkConfigurationError(last, "Monitoring:Endpoints:0:Target must be an absolute http(s) URL.");
        static MetricsBatchDto Batch(string batchId, DateTime batchedAt, EndpointStatus entry) => AgentPayloads.MinimalBatch() with
        {
            BatchId = batchId,
            BatchedAt = AgentPayloads.Utc(batchedAt),
            EndpointStatuses =
            [
                new EndpointStatusDto(entry.Timestamp.ToString("O"), entry.Name, entry.Type, entry.Target, entry.Status, entry.IsUp,
                    entry.ResponseTimeMs, entry.CertificateDaysLeft, entry.Detail),
            ],
        };
        var older = Batch("6c1e5f2a-0d3b-4c7e-9a8f-1b2c3d4e5f60", checkedAt.AddSeconds(10), last);
        var newer = Batch("7d2f6a3b-1e4c-4d8f-8b9a-2c3d4e5f6a71", checkedAt.AddSeconds(40), marked);

        (await client.SendMetricsBatchAsync(older, ct)).Should().BeTrue();
        (await client.SendMetricsBatchAsync(newer, ct)).Should().BeTrue();
        portal.Agent.Endpoints["shop-home"].Status.Should().Be("ConfigurationError", "an equal timestamp from a later batch replaces the entry");

        // The older batch arrives again late, as from the agent's outbox: the status must not go back to Up.
        (await client.SendMetricsBatchAsync(older with { BatchId = "8e3a7b4c-2f5d-4e9a-9c0b-3d4e5f6a7b82" }, ct)).Should().BeTrue();
        var entry = portal.Agent.Endpoints["shop-home"];
        entry.Status.Should().Be("ConfigurationError");
        entry.IsUp.Should().BeTrue("isUp is the last result's");
        entry.Detail.Should().Be("Configuration error: Monitoring:Endpoints:0:Target must be an absolute http(s) URL. Last result: Up: HTTP 200");
        Sample.PortalState.EndpointHealth(entry).Should().Be("not checked (configuration error)");
        portal.State.Describe(portal.Agent)["endpoints"]![0]!["health"]!.GetValue<string>().Should().Be("not checked (configuration error)");
    }

    [Fact]
    public async Task StaleEntry_IsShownStale_ALateOlderBatchDoesNotMakeItFresh_AndAMissingIsStaleIsJudgedFromTheTimestamp()
    {
        var portal = await StartPortalAsync("");
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;
        var readAt = AgentPayloads.Now.AddMinutes(-30);
        var reading = new DiskMetricDto(AgentPayloads.Utc(readAt), @"D:\", 512, 301.7, 41.1);
        MetricsBatchDto Batch(string id, DateTime batchedAt, bool? isStale) => AgentPayloads.MinimalBatch() with
        {
            BatchId = id,
            BatchedAt = AgentPayloads.Utc(batchedAt),
            CpuMetrics = [],
            DiskMetrics = [reading with { IsStale = isStale }],
        };
        string Freshness() => portal.State.Describe(portal.Agent)["disks"]![0]!["freshness"]!.GetValue<string>();
        var early = Batch("0f1e2d3c-4b5a-4968-8776-655443322110", readAt.AddMinutes(5), false);

        (await client.SendMetricsBatchAsync(early, ct)).Should().BeTrue();
        Freshness().Should().Be("fresh");

        // The same reading 20 minutes later: still listed, not read again, now older than its window.
        (await client.SendMetricsBatchAsync(Batch("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", readAt.AddMinutes(20), true), ct)).Should().BeTrue();
        Freshness().Should().Be($"stale (no reading since {new DateTimeOffset(readAt):O})");
        portal.Agent.Disks[@"D:\"].FreeGb.Should().Be(301.7, "the last reading is kept");

        // The early batch delivered again late (another batchId, as after a retry from an older outbox).
        (await client.SendMetricsBatchAsync(early with { BatchId = "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e" }, ct)).Should().BeTrue();
        Freshness().Should().StartWith("stale", "an older batch must not make the entry fresh again");

        // An agent that predates isStale: unknown, so the portal judges from the timestamp.
        var older = await StartPortalAsync("");
        (await older.Client().SendMetricsBatchAsync(Batch("3c4d5e6f-7a8b-4c9d-8e0f-2a3b4c5d6e7f", readAt.AddMinutes(20), null), ct)).Should().BeTrue();
        older.State.Describe(older.Agent)["disks"]![0]!["freshness"]!.GetValue<string>()
            .Should().StartWith("stale").And.Contain("judged from the timestamp");
        (await older.Client().SendMetricsBatchAsync(Batch("4d5e6f7a-8b9c-4d0e-9f1a-3b4c5d6e7f80", readAt.AddMinutes(25), null) with
        {
            DiskMetrics = [new DiskMetricDto(AgentPayloads.Utc(readAt.AddMinutes(24)), @"D:\", 512, 301.0, 41.2)],
        }, ct)).Should().BeTrue();
        older.State.Describe(older.Agent)["disks"]![0]!["freshness"]!.GetValue<string>()
            .Should().Be("fresh (judged from the timestamp, the agent sends no isStale)");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/watchtower")]
    public async Task Events_OpenAndCloseAnAlert_AndARetryIsADuplicate(string pathBase)
    {
        var portal = await StartPortalAsync(pathBase);
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;
        var opened = AgentPayloads.FullEvent();
        var resolved = opened with
        {
            EventId = "1e4b7a90-6c2d-4e8f-b1a3-5d7c9e0f2a33",
            Timestamp = AgentPayloads.Utc(AgentPayloads.Now),
            Description = "Resolved after 5 min: CPU at 99.6% for over 300s",
            TopProcesses = null,
            Resolved = true,
        };

        (await client.SendEventAsync(opened, ct)).Should().BeTrue();
        portal.Agent.OpenAlerts.Should().ContainKey("cpu");

        (await client.SendEventAsync(resolved, ct)).Should().BeTrue();
        (await client.SendEventAsync(opened, ct)).Should().BeTrue("a retried event is still answered 2xx");
        (await client.SendEventAsync(AgentPayloads.MinimalEvent(), ct)).Should().BeTrue();

        var agent = portal.Agent;
        agent.OpenAlerts.Should().BeEmpty();
        agent.Events.Should().HaveCount(3);
        agent.DuplicateEvents.Should().Be(1);
    }

    private static AgentEventDto EndpointEvent(int minute, IncidentType type, bool resolved, string description) => new(
        Guid.NewGuid().ToString(), AgentPayloads.Utc(AgentPayloads.Now.AddMinutes(minute)), (int)type, description, null,
        "endpoint:shop-home", resolved);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alerts_ACloseAndAnOpeningWithTheSameTimestamp_LeaveTheNewAlertOpen(bool openingArrivesFirst)
    {
        // Down -> Expiring: the agent sends EndpointRecovered (resolved) and CertificateExpiring
        // (opened) with the same timestamp; they may arrive in either order.
        var portal = await StartPortalAsync("");
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;
        var recovered = EndpointEvent(1, IncidentType.EndpointRecovered, true, "Endpoint 'shop-home' recovered");
        var expiring = EndpointEvent(1, IncidentType.CertificateExpiring, false, "Certificate expires in 9 days");

        (await client.SendEventAsync(EndpointEvent(0, IncidentType.EndpointDown, false, "Endpoint 'shop-home' is down"), ct)).Should().BeTrue();
        foreach (var evt in openingArrivesFirst ? new[] { expiring, recovered } : [recovered, expiring])
            (await client.SendEventAsync(evt, ct)).Should().BeTrue();

        var alert = portal.Agent.OpenAlerts.Should().ContainKey("endpoint:shop-home").WhoseValue;
        alert.IncidentType.Should().Be((int)IncidentType.CertificateExpiring);
        alert.OpenedAt.Should().Be(DateTimeOffset.Parse(expiring.Timestamp, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Alerts_AnOlderEscalationArrivingLate_DoesNotOverwriteTheNewerState()
    {
        var portal = await StartPortalAsync("");
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;

        (await client.SendEventAsync(EndpointEvent(0, IncidentType.CertificateExpiring, false, "expires in 9 days"), ct)).Should().BeTrue();
        (await client.SendEventAsync(EndpointEvent(5, IncidentType.EndpointDown, false, "down"), ct)).Should().BeTrue();
        (await client.SendEventAsync(EndpointEvent(2, IncidentType.CertificateExpiring, false, "expires in 8 days (late)"), ct)).Should().BeTrue();

        var alert = portal.Agent.OpenAlerts["endpoint:shop-home"];
        alert.IncidentType.Should().Be((int)IncidentType.EndpointDown);
        alert.Description.Should().Be("down");
        portal.Agent.Events.Should().HaveCount(3, "the late event is kept as history");

        // A close older than the last transition does not end the newer state either.
        (await client.SendEventAsync(EndpointEvent(3, IncidentType.EndpointRecovered, true, "recovered (late)"), ct)).Should().BeTrue();
        portal.Agent.OpenAlerts.Should().ContainKey("endpoint:shop-home");
        (await client.SendEventAsync(EndpointEvent(6, IncidentType.EndpointRecovered, true, "recovered"), ct)).Should().BeTrue();
        portal.Agent.OpenAlerts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/watchtower")]
    public async Task Command_RoundTrip_PendingAcknowledgeResult(string pathBase)
    {
        var portal = await StartPortalAsync(pathBase);
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;
        (await client.PollCommandsAsync(ct)).Should().BeEmpty("nothing is queued (204)");

        var queued = portal.State.QueueCommand(portal.Agent, (int)ActionType.ServiceRestart,
            new Dictionary<string, string> { ["ServiceName"] = "W3SVC" }, null);

        var pending = await client.PollCommandsAsync(ct);
        var command = pending.Should().ContainSingle().Subject;
        command.CommandId.Should().Be(queued.CommandId);
        command.ActionType.Should().Be((int)ActionType.ServiceRestart);
        command.Parameters.Should().Contain("ServiceName", "W3SVC");
        DateTimeOffset.Parse(command.IssuedAt, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await client.PollCommandsAsync(ct)).Should().ContainSingle("a command is re-delivered until it is acknowledged");

        (await client.AcknowledgeCommandAsync(AgentPayloads.Ack(command.CommandId), ct)).Should().BeTrue();
        portal.Agent.Commands[command.CommandId].State.Should().Be(Sample.CommandState.Acknowledged);
        (await client.PollCommandsAsync(ct)).Should().BeEmpty();

        var result = new CommandResultDto(command.CommandId, true, "Service 'W3SVC' restarted successfully", AgentPayloads.Utc(DateTime.UtcNow));
        (await client.ReportCommandResultAsync(result, ct)).Should().BeTrue();
        (await client.ReportCommandResultAsync(result with { Success = false, Message = "late copy" }, ct))
            .Should().BeTrue("a repeated result is answered 2xx");

        var record = portal.Agent.Commands[command.CommandId];
        record.State.Should().Be(Sample.CommandState.Succeeded, "the first result wins");
        record.ResultMessage.Should().Be("Service 'W3SVC' restarted successfully");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/watchtower")]
    public async Task Config_FetchApplyAcknowledge_ThenNoChange(string pathBase)
    {
        var portal = await StartPortalAsync(pathBase);
        var client = portal.Client();
        var ct = TestContext.Current.CancellationToken;
        (await client.FetchConfigurationAsync(null, ct)).HasUpdate.Should().BeFalse("no configuration yet (204)");

        // Push the agent's full configuration through the portal's admin API (which repeats the
        // agent's validation rules), then fetch it with the agent's client.
        var configJson = JsonSerializer.Serialize(AgentPayloads.FullConfig(), AgentClientHarness.WireOptions);
        using var admin = portal.Admin();
        var put = await admin.PutAsync($"admin/agents/{portal.AgentId}/config",
            new StringContent(configJson, System.Text.Encoding.UTF8, "application/json"), ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(ct));
        var version = (await put.Content.ReadFromJsonAsync<JsonObject>(ct))!["version"]!.GetValue<string>();

        var (hasUpdate, config, fetchedVersion) = await client.FetchConfigurationAsync(null, ct);
        hasUpdate.Should().BeTrue();
        fetchedVersion.Should().Be(version);
        JsonSerializer.Serialize(config, AgentClientHarness.WireOptions).Should().Be(configJson);
        RemoteConfigValidator.Validate(config!, out var error, new DateTimeOffset(AgentPayloads.Now, TimeSpan.Zero))
            .Should().BeTrue(error);

        (await client.FetchConfigurationAsync(version, ct)).HasUpdate.Should().BeFalse("the agent is current (204)");
        (await client.AcknowledgeConfigAsync(version, ct)).Should().BeTrue();
        portal.Agent.AcknowledgedConfigVersion.Should().Be(version);
    }

    [Fact]
    public async Task WrongToken_IsRefused_AndNothingIsStored()
    {
        var portal = await StartPortalAsync("");
        var client = portal.Client(token: "not-the-token");
        var ct = TestContext.Current.CancellationToken;
        portal.State.QueueCommand(portal.Agent, (int)ActionType.ForceMetricSnapshot, null, null);

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), ct)).Should().BeFalse();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), ct)).Should().BeFalse();
        (await client.PollCommandsAsync(ct)).Should().BeEmpty();

        portal.Agent.BatchesReceived.Should().Be(0);
        portal.Agent.Events.Should().BeEmpty();
        var response = await portal.Server.CreateClient().PostAsJsonAsync("api/v1/agent/events", new { }, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
    }

    [Fact]
    public async Task BaseUrl_WithoutThePortalsPathPrefix_FailsInsteadOfReachingTheApi()
    {
        var portal = await StartPortalAsync("/watchtower");
        var client = portal.Client(baseUrl: "http://localhost/");
        var ct = TestContext.Current.CancellationToken;

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), ct)).Should().BeFalse();
        portal.Agent.BatchesReceived.Should().Be(0);
    }

    // ── the agent's real loops against the portal ───────────────────────────────────────────

    [Fact]
    public async Task OutboxLoop_DeliversQueuedEventsAndAMetricsBatch()
    {
        var portal = await StartPortalAsync("/watchtower");
        var ct = TestContext.Current.CancellationToken;
        var options = new StaticOptionsMonitor<PortalOptions>(AgentClientHarness.Options(portal.BaseUrl, portal.AgentId, portal.Token));
        using var outbox = new SqliteOutboxStore(Path.Combine(TempDirectory(), "outbox.db"));
        var now = DateTime.UtcNow;

        var metrics = new Mock<IMetricsStore>();
        metrics.Setup(m => m.GetCpuMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((2L, (IReadOnlyList<CpuMetric>)[new CpuMetric(now.AddSeconds(-10), 12.5), new CpuMetric(now.AddSeconds(-5), 14)]));
        metrics.Setup(m => m.GetRamMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((1L, (IReadOnlyList<RamMetric>)[new RamMetric(now.AddSeconds(-10), 61.2, 6355.4, 16384)]));
        metrics.Setup(m => m.GetLatestDiskMetrics()).Returns([new DiskMetric(now, @"C:\", 126.4, 40.2, 68.2)]);
        metrics.Setup(m => m.GetLatestDiskQueueMetrics()).Returns([new DiskQueueMetric(now, "0 C:", 0.4)]);
        metrics.Setup(m => m.GetLatestNetworkMetrics()).Returns([new NetworkMetric(now, "Ethernet", 0)]);
        metrics.Setup(m => m.GetLatestServiceStatuses()).Returns([new ServiceStatus(now, "W3SVC", "World Wide Web Publishing Service", "Running", true)]);
        metrics.Setup(m => m.GetLatestApplicationPoolStatuses()).Returns([new ApplicationPoolStatus(now, "DefaultAppPool", "Started", true)]);
        metrics.Setup(m => m.GetLatestEndpointStatuses()).Returns([new EndpointStatus(now, "shop-home", "Http", "https://shop.example.com/", "Up", true, 182, null, "HTTP 200")]);
        var telemetry = new Mock<IAgentTelemetry>();
        telemetry.Setup(t => t.Snapshot()).Returns(AgentPayloads.FullBatch().Agent!);

        var publisher = new PortalEventPublisher(outbox, NullLogger<PortalEventPublisher>.Instance);
        await publisher.PublishAlertEventAsync(new Incident(now.AddMinutes(-1), IncidentType.ServiceDown, "Service 'W3SVC' is Stopped"),
            "svc:W3SVC", false, ct);
        await publisher.PublishAlertEventAsync(new Incident(now, IncidentType.ServiceRestarted, "Service 'W3SVC' was restarted"),
            "svc:W3SVC", true, ct);

        var client = portal.Client();
        var worker = new OutboxDispatchWorker(client, outbox, outbox, metrics.Object, new MetricSnapshotTrigger(), options,
            TimeProvider.System, NullLogger<OutboxDispatchWorker>.Instance, telemetry.Object);
        await worker.RunCycleAsync(ct);

        var agent = portal.Agent;
        agent.Events.Should().HaveCount(2);
        agent.OpenAlerts.Should().BeEmpty("the service went down and came back");
        agent.BatchesReceived.Should().Be(1);
        agent.Cpu.Should().HaveCount(2);
        agent.Endpoints.Should().ContainKey("shop-home");
        agent.Telemetry!.MachineName.Should().Be("SRV-WEB-01");
        (await outbox.GetPendingMessagesAsync(100)).Should().BeEmpty("every message was delivered");
    }

    [Fact]
    public async Task CommandLoop_ReceivesAcknowledgesExecutesAndReports()
    {
        var portal = await StartPortalAsync("/watchtower");
        var ct = TestContext.Current.CancellationToken;
        var options = new StaticOptionsMonitor<PortalOptions>(AgentClientHarness.Options(portal.BaseUrl, portal.AgentId, portal.Token));
        using var store = new SqliteCommandStore(Path.Combine(TempDirectory(), "commands.db"));
        var trigger = new MetricSnapshotTrigger();
        var registry = new ActionHandlerRegistry(
            [new ForceMetricSnapshotHandler(trigger, NullLogger<ForceMetricSnapshotHandler>.Instance)],
            NullLogger<ActionHandlerRegistry>.Instance);
        var worker = new CommandPollingWorker(portal.Client(), store, registry, options,
            new StaticOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions()),
            new StaticOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions()),
            TimeProvider.System, NullLogger<CommandPollingWorker>.Instance);

        var queued = portal.State.QueueCommand(portal.Agent, (int)ActionType.ForceMetricSnapshot, null, null);
        await worker.RunCycleAsync(ct);

        var record = portal.Agent.Commands[queued.CommandId];
        record.State.Should().Be(Sample.CommandState.Succeeded, record.ResultMessage);
        record.ResultMessage.Should().Be("Metric snapshot triggered");
        record.AcknowledgedAt.Should().NotBeNull();
        trigger.TryConsume().Should().BeTrue("the agent really ran the command");

        await worker.RunCycleAsync(ct);
        trigger.TryConsume().Should().BeFalse("an executed command is not delivered or run again");
    }

    [Fact]
    public async Task CommandLoop_RejectsAFutureIssuedAt_WithAFailedResultAndNoAcknowledgement()
    {
        var portal = await StartPortalAsync("/watchtower");
        var ct = TestContext.Current.CancellationToken;
        var options = new StaticOptionsMonitor<PortalOptions>(AgentClientHarness.Options(portal.BaseUrl, portal.AgentId, portal.Token));
        using var store = new SqliteCommandStore(Path.Combine(TempDirectory(), "commands.db"));
        var trigger = new MetricSnapshotTrigger();
        var registry = new ActionHandlerRegistry(
            [new ForceMetricSnapshotHandler(trigger, NullLogger<ForceMetricSnapshotHandler>.Instance)],
            NullLogger<ActionHandlerRegistry>.Instance);
        var worker = new CommandPollingWorker(portal.Client(), store, registry, options,
            new StaticOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions()),
            new StaticOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions()),
            TimeProvider.System, NullLogger<CommandPollingWorker>.Instance);

        // A portal whose clock runs 10 minutes fast.
        var id = Guid.NewGuid().ToString();
        portal.Agent.Commands[id] = new Sample.CommandRecord
        {
            Command = new Sample.AgentCommand(id, (int)ActionType.ForceMetricSnapshot, [], null,
                DateTime.UtcNow.AddMinutes(10).ToString("O")),
        };
        await worker.RunCycleAsync(ct);

        var record = portal.Agent.Commands[id];
        record.State.Should().Be(Sample.CommandState.Failed);
        record.ResultMessage.Should().StartWith("Rejected: issuedAt ").And.Contain("is more than 300s in the future");
        record.AcknowledgedAt.Should().BeNull("a rejected command is never acknowledged");
        trigger.TryConsume().Should().BeFalse("a rejected command never runs");
    }

    [Fact]
    public async Task RemoteConfigLoop_FetchesAppliesAndAcknowledges()
    {
        var portal = await StartPortalAsync("/watchtower");
        var ct = TestContext.Current.CancellationToken;
        var options = new StaticOptionsMonitor<PortalOptions>(AgentClientHarness.Options(portal.BaseUrl, portal.AgentId, portal.Token));
        var data = TempDirectory();
        var paths = new WatchTowerPaths(data, data);
        using var store = new SqliteRemoteConfigStore(Path.Combine(data, "config.db"));
        var version = portal.State.SetConfig(portal.Agent,
            JsonNode.Parse("""{"cpu":{"thresholdPercent":90,"sustainedSeconds":600},"services":{"watchedServices":["W3SVC"]}}""")!.AsObject());

        var worker = new RemoteConfigWorker(portal.Client(), store, paths, new ConfigSyncTrigger(), options,
            TimeProvider.System, NullLogger<RemoteConfigWorker>.Instance);
        await worker.StartAsync(ct);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (portal.Agent.AcknowledgedConfigVersion != version && DateTime.UtcNow < deadline)
                await Task.Delay(50, ct);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        portal.Agent.AcknowledgedConfigVersion.Should().Be(version);
        (await store.GetCurrentVersionAsync()).Should().Be(version);
        File.ReadAllText(paths.RemoteConfigFilePath).Should().Contain("W3SVC");
    }
}