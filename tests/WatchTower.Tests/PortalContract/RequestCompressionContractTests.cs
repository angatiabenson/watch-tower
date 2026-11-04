using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Cli;
using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.Ui;
using WatchTower.Workers;

using Sample = WatchTower.Samples.MinimalPortal;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// Opt-in request compression (<c>VisibilityPortal:CompressRequests=Gzip</c>): the agent's real
/// <see cref="HttpsPortalClient"/> against MinimalPortal (which accepts gzip and br, or refuses
/// every compressed request with 415 when told to), and against test servers that refuse it
/// in other ways.
/// </summary>
public sealed class RequestCompressionContractTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private sealed record Portal(WebApplication App, Sample.PortalState State, Sample.CreateAgentResponse Enrollment)
    {
        public const string BaseUrl = "http://localhost/";

        public Sample.AgentRecord Agent => State.Find(Enrollment.AgentId)!;

        public PortalOptions Options(string compress = RequestCompressionModes.Gzip)
        {
            var options = AgentClientHarness.Options(BaseUrl, Enrollment.AgentId, Enrollment.Token);
            options.CompressRequests = compress;
            return options;
        }

        public HttpClient Raw()
        {
            var http = App.GetTestServer().CreateClient();
            http.DefaultRequestHeaders.Add("X-Agent-Id", Enrollment.AgentId);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Enrollment.Token);
            return http;
        }
    }

    private async Task<Portal> StartPortalAsync(bool acceptCompressed = true)
    {
        var app = Sample.MinimalPortalApp.Build(
            acceptCompressed ? [] : ["--AcceptCompressedRequests", "false"],
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Logging.ClearProviders();
            });
        await app.StartAsync(Ct);
        _portals.Add(app);
        var state = app.Services.GetRequiredService<Sample.PortalState>();
        return new Portal(app, state, state.CreateAgent(AgentClientHarness.AgentId));
    }

    private static (HttpsPortalClient Client, WireTap Wire, RecordingLoggerProvider Logs) Connect(
        Portal portal, PortalOptions options)
    {
        var logs = new RecordingLoggerProvider();
        var wire = new WireTap(portal.App.GetTestServer().CreateHandler());
        var client = AgentClientHarness.CreateClient(options, () => wire, logs.CreateLogger<HttpsPortalClient>());
        return (client, wire, logs);
    }

    private static IEnumerable<LogEntry> FallbackWarnings(RecordingLoggerProvider logs) =>
        logs.At(LogLevel.Warning).Where(e => e.Message.Contains(RequestCompression.FallbackWarning));

    // ── against MinimalPortal ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gzip_BatchesAndEventsAreCompressedAndStored_AndTheSmallRequestsAreNot()
    {
        var portal = await StartPortalAsync();
        var (client, wire, logs) = Connect(portal, portal.Options());

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();
        await client.AcknowledgeConfigAsync("v1", Ct);
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue("a retry is a duplicate, still 2xx");

        var agent = portal.Agent;
        agent.BatchesReceived.Should().Be(1);
        agent.DuplicateBatches.Should().Be(1);
        agent.Cpu.Should().HaveCount(2);
        agent.Endpoints["old-cert"].CertificateDaysLeft.Should().Be(-2.5);
        agent.Telemetry!.Version.Should().Be("1.6.0");
        agent.Events.Should().ContainSingle();

        var sent = wire.Requests;
        sent.Select(r => (r.Path, r.ContentEncoding)).Should().Equal(
            ("/api/v1/agent/metrics/batch", "gzip"),
            ("/api/v1/agent/events", "gzip"),
            ("/api/v1/agent/config/acknowledge", null),
            ("/api/v1/agent/metrics/batch", "gzip"));
        sent.Where(r => r.ContentEncoding == "gzip").Should().AllSatisfy(r =>
        {
            r.ContentLength.Should().BeNull("compressed bodies stream with chunked transfer encoding");
            r.ContentType.Should().Be("application/json; charset=utf-8");
            r.Status.Should().Be(HttpStatusCode.Accepted);
        });
        // The wire bytes are real gzip of the same JSON the uncompressed client sends.
        var json = JsonNode.Parse(Gunzip(sent[0].Body))!;
        json["batchId"]!.GetValue<string>().Should().Be(AgentPayloads.FullBatch().BatchId);
        json["agent"]!["version"]!.GetValue<string>().Should().Be("1.6.0");
        sent[0].Body.Length.Should().BeLessThan(Encoding.UTF8.GetByteCount(json.ToJsonString()));
        FallbackWarnings(logs).Should().BeEmpty();
    }

    [Fact]
    public async Task Off_IsTheDefault_AndSendsNothingCompressed()
    {
        var portal = await StartPortalAsync();
        new PortalOptions().CompressRequests.Should().Be(RequestCompressionModes.Off);
        var (client, wire, _) = Connect(portal, AgentClientHarness.Options(Portal.BaseUrl, portal.Enrollment.AgentId, portal.Enrollment.Token));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();

        wire.Requests.Should().AllSatisfy(r => r.ContentEncoding.Should().BeNull());
    }

    [Fact]
    public async Task PortalThatRefusesCompression_Gets415_AndTheAgentResendsUncompressedAndStops()
    {
        var portal = await StartPortalAsync(acceptCompressed: false);
        var (client, wire, logs) = Connect(portal, portal.Options());

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue("the uncompressed resend was accepted");
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(batchId: "c3f1a7e2-4b1d-4c55-9e0a-7f3b2d1c9e88"), Ct))
            .Should().BeTrue();

        wire.Requests.Select(r => (r.ContentEncoding, r.Status)).Should().Equal(
            ("gzip", HttpStatusCode.UnsupportedMediaType),
            (null, HttpStatusCode.Accepted),
            (null, HttpStatusCode.Accepted),
            (null, HttpStatusCode.Accepted));
        wire.Requests[0].AcceptEncoding.Should().Be("identity", "RFC 7694: a 415 names the encodings the portal reads");

        var agent = portal.Agent;
        agent.BatchesReceived.Should().Be(2);
        agent.DuplicateBatches.Should().Be(0, "the refused compressed request was not stored, so nothing is duplicated");
        agent.Events.Should().ContainSingle();
        FallbackWarnings(logs).Should().ContainSingle();
    }

    [Fact]
    public async Task OutboxLoop_AcrossTheFallback_DeliversEveryMessageExactlyOnce()
    {
        var portal = await StartPortalAsync(acceptCompressed: false);
        var options = portal.Options();
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-gzip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempPaths.Add(path);
        using var outbox = new SqliteOutboxStore(Path.Combine(path, "outbox.db"));
        var now = DateTime.UtcNow;

        var metrics = new Mock<IMetricsStore>();
        metrics.Setup(m => m.GetCpuMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((2L, (IReadOnlyList<CpuMetric>)[new CpuMetric(now.AddSeconds(-10), 12.5), new CpuMetric(now.AddSeconds(-5), 14)]));
        metrics.Setup(m => m.GetRamMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((1L, (IReadOnlyList<RamMetric>)[new RamMetric(now.AddSeconds(-10), 61.2, 6355.4, 16384)]));
        metrics.Setup(m => m.GetLatestDiskMetrics()).Returns([new DiskMetric(now, @"C:\", 126.4, 40.2, 68.2)]);
        metrics.Setup(m => m.GetLatestDiskQueueMetrics()).Returns([]);
        metrics.Setup(m => m.GetLatestNetworkMetrics()).Returns([]);
        metrics.Setup(m => m.GetLatestServiceStatuses()).Returns([]);
        metrics.Setup(m => m.GetLatestApplicationPoolStatuses()).Returns([]);
        metrics.Setup(m => m.GetLatestEndpointStatuses()).Returns([]);

        var publisher = new PortalEventPublisher(outbox, NullLogger<PortalEventPublisher>.Instance);
        await publisher.PublishAlertEventAsync(new Incident(now.AddMinutes(-1), IncidentType.ServiceDown, "Service 'W3SVC' is Stopped"),
            "svc:W3SVC", false, Ct);
        await publisher.PublishAlertEventAsync(new Incident(now, IncidentType.ServiceRestarted, "Service 'W3SVC' was restarted"),
            "svc:W3SVC", true, Ct);

        var (client, wire, logs) = Connect(portal, options);
        var worker = new OutboxDispatchWorker(client, outbox, outbox, metrics.Object, new MetricSnapshotTrigger(),
            new StaticOptionsMonitor<PortalOptions>(options), TimeProvider.System, NullLogger<OutboxDispatchWorker>.Instance);
        await worker.RunCycleAsync(Ct);

        var agent = portal.Agent;
        agent.Events.Should().HaveCount(2);
        agent.DuplicateEvents.Should().Be(0);
        agent.BatchesReceived.Should().Be(1);
        agent.DuplicateBatches.Should().Be(0);
        (await outbox.GetPendingMessagesAsync(100)).Should().BeEmpty("every message was delivered");
        wire.Requests.Count(r => r.ContentEncoding == "gzip").Should().Be(1, "only the first request tried gzip");
        wire.Requests.Should().HaveCount(4, "one refused compressed request, then two events and a batch");
        FallbackWarnings(logs).Should().ContainSingle();
    }

    [Fact]
    public async Task MinimalPortal_DecodesBrotliToo()
    {
        var portal = await StartPortalAsync();
        using var http = portal.Raw();
        var json = JsonSerializer.SerializeToUtf8Bytes(AgentPayloads.FullEvent(), AgentClientHarness.WireOptions);
        using var buffer = new MemoryStream();
        await using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(json);

        using var response = await http.PostAsync("api/v1/agent/events", Encoded(buffer.ToArray(), "br"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        portal.Agent.Events.Should().ContainSingle();
    }

    [Theory]
    [InlineData("deflate")]
    [InlineData("gzip, gzip")]
    [InlineData("compress")]
    public async Task MinimalPortal_RefusesOtherEncodingsWith415(string encoding)
    {
        var portal = await StartPortalAsync();
        using var http = portal.Raw();

        using var response = await http.PostAsync("api/v1/agent/events", Encoded([1, 2, 3], encoding), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        response.Headers.GetValues("Accept-Encoding").Should().ContainSingle().Which.Should().Be("gzip, br");
    }

    [Fact]
    public async Task MinimalPortal_RefusesABodyThatInflatesPastTheCapWith413_AndTheAgentResendsItUncompressed()
    {
        var portal = await StartPortalAsync();
        portal.App.Services.GetRequiredService<Sample.PortalOptions>().MaxDecompressedBodyBytes = 1_000;
        using var http = portal.Raw();

        // 1 MB of zeros gzips to about 1 KB: a body that is small on the wire and huge inflated.
        using (var bomb = await http.PostAsync("api/v1/agent/events", Encoded(Gzip(new byte[1_000_000]), "gzip"), Ct))
            bomb.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        portal.Agent.Events.Should().BeEmpty();

        // The full batch is ~2 KB of JSON: above the inflated cap, below Kestrel's own limit.
        var (client, wire, logs) = Connect(portal, portal.Options());
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.MinimalEvent(), Ct)).Should().BeTrue();

        wire.Requests.Select(r => (r.ContentEncoding, r.Status)).Should().Equal(
            ("gzip", HttpStatusCode.RequestEntityTooLarge),
            (null, HttpStatusCode.Accepted),
            ("gzip", HttpStatusCode.Accepted));
        FallbackWarnings(logs).Should().BeEmpty("a 413 is about the size, so compression stays on");
        portal.Agent.BatchesReceived.Should().Be(1);
    }

    [Fact]
    public async Task MinimalPortal_RefusesDamagedGzipWith400()
    {
        var portal = await StartPortalAsync();
        using var http = portal.Raw();

        using var response = await http.PostAsync("api/v1/agent/events", Encoded("not gzip at all"u8.ToArray(), "gzip"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── against test servers that refuse in other ways ──────────────────────────────────────

    private static (HttpsPortalClient Client, FakePortal Handler, RecordingLoggerProvider Logs) Fake(
        Func<bool, HttpResponseMessage> respond, IOptionsMonitor<PortalOptions>? monitor = null)
    {
        var handler = new FakePortal(respond);
        var logs = new RecordingLoggerProvider();
        monitor ??= new StaticOptionsMonitor<PortalOptions>(GzipOptions());
        var services = new ServiceCollection();
        services.AddSingleton(monitor);
        ServiceCollectionExtensions.AddPortalHttpClient(services, monitor.CurrentValue.BaseUrl)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var client = new HttpsPortalClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), monitor,
            logs.CreateLogger<HttpsPortalClient>());
        return (client, handler, logs);
    }

    private static PortalOptions GzipOptions(string baseUrl = "https://portal.example.com")
    {
        var options = AgentClientHarness.Options(baseUrl);
        options.CompressRequests = "gzip"; // case-insensitive
        return options;
    }

    private static IEnumerable<bool> Compressed(FakePortal handler) => handler.Requests.Select(r => r.Gzip);

    /// <summary>Answers <paramref name="refusal"/> to compressed requests, 200 otherwise.</summary>
    private static Func<bool, HttpResponseMessage> RefuseGzip(HttpStatusCode refusal) => gzip =>
        new HttpResponseMessage(gzip ? refusal : HttpStatusCode.OK);

    [Fact]
    public async Task Status415_ResendsOnceUncompressed_ThenStaysUncompressed_AndWarnsOnce()
    {
        var (client, handler, logs) = Fake(RefuseGzip(HttpStatusCode.UnsupportedMediaType));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();
        (await client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), Ct)).Should().BeTrue();

        Compressed(handler).Should().Equal(true, false, false, false);
        handler.Requests[1].Body.Should().Contain("\"batchId\":\"0b6f3c1e-8f6c-4d8e-9a43-3c2a7d5e9b11\"", "the same batch was resent");
        FallbackWarnings(logs).Should().ContainSingle().Which.Message.Should().Contain("415");
    }

    [Fact]
    public async Task Status415_WhenTheUncompressedResendFailsToo_TheCallFails_AndCompressionStaysOff()
    {
        var calls = 0;
        var (client, handler, logs) = Fake(gzip => ++calls == 2
            ? throw new HttpRequestException("connection reset")
            : RefuseGzip(HttpStatusCode.UnsupportedMediaType)(gzip));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeFalse("not delivered: the outbox retries it");
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();

        Compressed(handler).Should().Equal(true, false, false);
        FallbackWarnings(logs).Should().ContainSingle();
    }

    [Fact]
    public async Task Status400_CausedByTheEncoding_SwitchesCompressionOff()
    {
        // A portal without support that tries to parse the gzip bytes as JSON answers 400.
        var (client, handler, logs) = Fake(RefuseGzip(HttpStatusCode.BadRequest));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();

        Compressed(handler).Should().Equal(true, false, false);
        FallbackWarnings(logs).Should().ContainSingle().Which.Message.Should().Contain("400");
    }

    [Fact]
    public async Task Status400_ThatTheUncompressedBodyGetsToo_IsLeftAlone()
    {
        var (client, handler, logs) = Fake(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeFalse();
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeFalse();

        Compressed(handler).Should().Equal(true, false, true, false);
        FallbackWarnings(logs).Should().BeEmpty("the encoding was not the cause");
    }

    [Fact]
    public async Task Status400_AfterThePortalAcceptedCompressedRequests_IsNotBlamedOnTheEncoding()
    {
        var calls = 0;
        var (client, handler, logs) = Fake(_ => new HttpResponseMessage(
            ++calls == 2 ? HttpStatusCode.BadRequest : HttpStatusCode.OK));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeFalse("400 counts as failed; the outbox retries");
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();

        Compressed(handler).Should().Equal(true, true, true);
        FallbackWarnings(logs).Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OtherFailures_AreNotResent(HttpStatusCode status)
    {
        var (client, handler, logs) = Fake(_ => new HttpResponseMessage(status));

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeFalse();

        Compressed(handler).Should().Equal(true);
        FallbackWarnings(logs).Should().BeEmpty();
    }

    [Fact]
    public async Task AfterAFallback_ChangingTheSettingOrThePortalUrl_TurnsCompressionBackOn()
    {
        var monitor = new MutableOptionsMonitor<PortalOptions>(GzipOptions());
        var refuse = true;
        var (client, handler, logs) = Fake(
            gzip => refuse ? RefuseGzip(HttpStatusCode.UnsupportedMediaType)(gzip) : new HttpResponseMessage(HttpStatusCode.OK),
            monitor);

        await client.SendEventAsync(AgentPayloads.FullEvent(), Ct); // 415, resent, off
        await client.SendEventAsync(AgentPayloads.FullEvent(), Ct); // stays off
        refuse = false;

        monitor.Set(Copy(monitor.CurrentValue, compress: RequestCompressionModes.Off));
        await client.SendEventAsync(AgentPayloads.FullEvent(), Ct);
        monitor.Set(Copy(monitor.CurrentValue, compress: RequestCompressionModes.Gzip));
        await client.SendEventAsync(AgentPayloads.FullEvent(), Ct); // the operator turned it on again

        Compressed(handler).Should().Equal(true, false, false, false, true);

        // A new portal URL starts over as well (without any OnChange notification).
        refuse = true;
        var (client2, handler2, _) = Fake(gzip =>
            refuse ? RefuseGzip(HttpStatusCode.UnsupportedMediaType)(gzip) : new HttpResponseMessage(HttpStatusCode.OK),
            monitor);
        await client2.SendEventAsync(AgentPayloads.FullEvent(), Ct);
        refuse = false;
        monitor.Value = Copy(monitor.CurrentValue, baseUrl: "https://new-portal.example.com");
        await client2.SendEventAsync(AgentPayloads.FullEvent(), Ct);

        Compressed(handler2).Should().Equal(true, false, true);
        FallbackWarnings(logs).Should().HaveCount(1);
    }

    private static PortalOptions Copy(PortalOptions o, string? compress = null, string? baseUrl = null)
    {
        var copy = AgentClientHarness.Options(baseUrl ?? o.BaseUrl);
        copy.CompressRequests = compress ?? o.CompressRequests;
        return copy;
    }

    /// <summary>A portal stand-in: records each request (body decompressed) and answers via <c>respond(gzip)</c>.</summary>
    private sealed class FakePortal(Func<bool, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<(bool Gzip, string Body, string Method, string Path)> _requests = new();

        public IReadOnlyList<(bool Gzip, string Body, string Method, string Path)> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var gzip = request.Content?.Headers.ContentEncoding.Contains("gzip") == true;
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (_requests)
                _requests.Add((gzip, gzip ? Gunzip(bytes) : Encoding.UTF8.GetString(bytes), request.Method.Method,
                    request.RequestUri!.AbsolutePath));
            var response = respond(gzip);
            response.RequestMessage = request;
            return response;
        }
    }

    // ── the spec ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Spec_DeclaresContentEncoding_OnExactlyTheOperationsTheAgentCompresses()
    {
        var (client, handler, _) = Fake(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct);
        await client.SendEventAsync(AgentPayloads.FullEvent(), Ct);
        await client.AcknowledgeCommandAsync(AgentPayloads.Ack("c-1"), Ct);
        await client.ReportCommandResultAsync(AgentPayloads.Result("c-1"), Ct);
        await client.AcknowledgeConfigAsync("v1", Ct);

        var compressed = handler.Requests.Where(r => r.Gzip)
            .Select(r => PortalSpec.Operations.Single(o => o.Matches(r.Method, r.Path)).OperationId);
        var declared = PortalSpec.Operations
            .Where(o => (o.Operation["parameters"]?.AsArray() ?? []).Select(p => PortalSpec.Resolve(p!))
                .Any(p => (string)p["name"]! == "Content-Encoding" && (string)p["in"]! == "header"))
            .Select(o => o.OperationId);
        compressed.Should().BeEquivalentTo(declared);
        handler.Requests.Should().HaveCount(5, "every POST was made once");
    }

    [Fact]
    public void Spec_EnrollmentCodePayload_DescribesAcceptsGzip()
    {
        const string json = """{"v":1,"url":"https://p.example","agentId":"a","token":"t","requireTls":true,"acceptsGzip":true}""";

        PortalSpec.Validate("EnrollmentCodePayload", json, SchemaMode.Strict).Should().BeEmpty();
        ((string?)PortalSpec.Schemas["EnrollmentCodePayload"]!["properties"]!["acceptsGzip"]!["x-since"])
            .Should().Be("after-1.0.20");
    }

    // ── options, validation, enrollment ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Off", true)]
    [InlineData("gzip", true)]
    [InlineData(" GZIP ", true)]
    [InlineData("", true)]
    [InlineData("zip", false)]
    [InlineData("br", false)]
    public void Validator_AcceptsOnlyOffAndGzip(string value, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VisibilityPortal:Enabled"] = "true",
            ["VisibilityPortal:AgentId"] = "a",
            ["VisibilityPortal:AgentToken"] = "t",
            ["VisibilityPortal:BaseUrl"] = "https://portal.example.com",
            ["VisibilityPortal:CompressRequests"] = value,
        }).Build();

        var errors = StartupValidator.Validate(config).Errors.Where(e => e.Contains("CompressRequests"));

        if (valid)
            errors.Should().BeEmpty();
        else
            errors.Should().ContainSingle().Which.Should().Contain("Off or Gzip");
    }

    [Fact]
    public void ConsoleSchema_OffersTheSetting()
    {
        var field = WatchTower.Ui.ConfigSchema.Sections.SelectMany(s => s.Fields)
            .Single(f => f.Key == "VisibilityPortal:CompressRequests");

        field.Options.Should().Equal("Off", "Gzip");
        field.RestartRequired.Should().BeFalse("the clients read it on every request");
    }

    private static string CodeFromJson(string json) =>
        EnrollmentCode.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Theory]
    [InlineData("""{"v":1,"url":"https://p.example","agentId":"a","token":"t","requireTls":true,"acceptsGzip":true}""", true)]
    [InlineData("""{"v":1,"url":"https://p.example","agentId":"a","token":"t","requireTls":true,"acceptsGzip":false}""", false)]
    [InlineData("""{"v":1,"url":"https://p.example","agentId":"a","token":"t","requireTls":true}""", false)]
    [InlineData("""{"v":1,"url":"https://p.example","agentId":"a","token":"t","acceptsGzip":"yes"}""", false)]
    [InlineData("""{"v":1,"url":"https://p.example","agentId":"a","token":"t","acceptsGzip":null}""", false)]
    [InlineData("""{"v":1,"url":"https://p.example","agentId":"a","token":"t","acceptsGzip":true,"acceptsBrotli":true,"x":{"y":[1]}}""", true)]
    public void EnrollmentCode_ReadsTheOptionalAcceptsGzip_AndIgnoresUnknownProperties(string json, bool acceptsGzip)
    {
        EnrollmentCode.TryParse(CodeFromJson(json), out var code, out var error).Should().BeTrue(error);

        code.AcceptsGzip.Should().Be(acceptsGzip);
        code.Url.Should().Be("https://p.example");
        EnrollmentCode.TryParse(code.Encode(), out var again, out _).Should().BeTrue();
        again.Should().Be(code);

        var patch = code.ToSettingsPatch()["VisibilityPortal"]!.AsObject();
        if (acceptsGzip)
            patch["CompressRequests"]!.GetValue<string>().Should().Be("Gzip");
        else
            patch.ContainsKey("CompressRequests").Should().BeFalse("a code never turns compression off");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MinimalPortal_SaysAcceptsGzipInItsCodes_OnlyWhenItAcceptsCompressedRequests(bool accept)
    {
        var portal = await StartPortalAsync(accept);

        EnrollmentCode.TryParse(portal.Enrollment.EnrollmentCode, out var code, out _).Should().BeTrue();

        code.AcceptsGzip.Should().Be(accept);
    }

    [Fact]
    public void CliEnroll_TurnsCompressionOnForACodeThatSaysSo_AndNeverOff()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();

        WatchTowerCli.MergeValidated(dir.Paths,
            new EnrollmentCode("https://portal.example.com", "srv-01", "tok", true, AcceptsGzip: true).ToSettingsPatch(), "test");
        Setting(dir).Should().Be("Gzip");

        WatchTowerCli.MergeValidated(dir.Paths,
            new EnrollmentCode("https://other.example.com", "srv-01", "tok", true).ToSettingsPatch(), "test");
        Setting(dir).Should().Be("Gzip", "a code without acceptsGzip leaves the operator's choice");

        static string? Setting(TestDataDirectory dir) =>
            JsonNode.Parse(File.ReadAllText(dir.Paths.SettingsFilePath))!["VisibilityPortal"]?["CompressRequests"]?.GetValue<string>();
    }

    [Fact]
    public async Task ConsoleEnroll_TurnsCompressionOnForACodeThatSaysSo_AndNeverOff()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var gzipCode = new EnrollmentCode("https://portal.example.com", "srv-01", "tok", true, AcceptsGzip: true).Encode();
        (await client.PostAsJsonAsync("api/actions/enroll", new { code = gzipCode }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        Setting().Should().Be("Gzip");

        var plainCode = new EnrollmentCode("https://portal.example.com", "srv-01", "tok", true).Encode();
        (await client.PostAsJsonAsync("api/actions/enroll", new { code = plainCode }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        Setting().Should().Be("Gzip", "a code without acceptsGzip leaves the operator's choice");

        string? Setting() => JsonNode.Parse(File.ReadAllText(host.Directory.Paths.SettingsFilePath))!
            ["VisibilityPortal"]?["CompressRequests"]?.GetValue<string>();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static ByteArrayContent Encoded(byte[] body, string encoding)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var e in encoding.Split(',', StringSplitOptions.TrimEntries))
            content.Headers.ContentEncoding.Add(e);
        return content;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data);
        return buffer.ToArray();
    }

    private static string Gunzip(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>One request as it went over the wire to MinimalPortal, with the answer.</summary>
    private sealed record WireRequest(
        string Path, string? ContentEncoding, long? ContentLength, string? ContentType, byte[] Body, HttpStatusCode Status,
        string? AcceptEncoding);

    /// <summary>Sits between the agent's pipeline and the TestServer and records the raw requests.</summary>
    private sealed class WireTap(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly List<WireRequest> _requests = new();

        public IReadOnlyList<WireRequest> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var length = request.Content?.Headers.ContentLength;
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var encoding = request.Content?.Headers.ContentEncoding.Count > 0
                ? string.Join(", ", request.Content.Headers.ContentEncoding)
                : null;
            var contentType = request.Content?.Headers.ContentType?.ToString();

            // Forward what was recorded (the original content was consumed above).
            if (request.Content is not null)
            {
                var copy = new ByteArrayContent(body);
                foreach (var header in request.Content.Headers)
                    copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                copy.Headers.ContentLength = body.Length;
                request.Content = copy;
            }

            var response = await base.SendAsync(request, cancellationToken);
            var acceptEncoding = response.Headers.TryGetValues("Accept-Encoding", out var values) ? string.Join(", ", values) : null;
            lock (_requests)
                _requests.Add(new WireRequest(request.RequestUri!.AbsolutePath, encoding, length, contentType, body,
                    response.StatusCode, acceptEncoding));
            return response;
        }
    }
}

/// <summary>An <see cref="IOptionsMonitor{T}"/> whose value tests change, notifying listeners via <see cref="Set"/>.</summary>
public sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = new();

    /// <summary>Changes the value without notifying (as if the change notification were lost).</summary>
    public T Value { get; set; } = value;

    public T CurrentValue => Value;

    public T Get(string? name) => Value;

    public void Set(T newValue)
    {
        Value = newValue;
        foreach (var listener in _listeners.ToList())
            listener(newValue, Options.DefaultName);
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}