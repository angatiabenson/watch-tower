using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using WatchTower.Configuration;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// The agent behaviour the spec and the guides promise to portal authors, pinned against the real
/// client and command loop: command freshness (<c>issuedAt</c>) and its rejection result, and the
/// handling of redirects.
/// </summary>
public sealed class AgentBehaviourContractTests : IDisposable
{
    private static readonly DateTimeOffset AgentNow = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower-contract-cmd-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    // ── issuedAt ─────────────────────────────────────────────────────────────────────────────

    private sealed record CommandRun(IReadOnlyList<RecordedRequest> Requests, RecordingLoggerProvider Logs)
    {
        public IReadOnlyList<RecordedRequest> Acks => Requests.Where(r => r.RawPath.EndsWith("/acknowledge", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<RecordedRequest> Results => Requests.Where(r => r.RawPath.EndsWith("/result", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Runs the real command loop <paramref name="cycles"/> times against a portal returning <paramref name="pending"/>.</summary>
    private async Task<CommandRun> RunCommandLoopAsync(JsonArray pending, int cycles = 1)
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? RecordingHandler.Json(pending.ToJsonString())
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var options = AgentClientHarness.Options("https://portal.example.com");
        var logs = new RecordingLoggerProvider();
        var time = new FakeTimeProvider(AgentNow);
        using var store = new SqliteCommandStore(_dbPath);
        var registry = new ActionHandlerRegistry(
            [new ForceMetricSnapshotHandler(new MetricSnapshotTrigger(), NullLogger<ForceMetricSnapshotHandler>.Instance)],
            NullLogger<ActionHandlerRegistry>.Instance);
        var worker = new CommandPollingWorker(
            AgentClientHarness.CreateClient(options, () => handler), store, registry,
            new StaticOptionsMonitor<PortalOptions>(options),
            new StaticOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions()),
            new StaticOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions()),
            time, logs.CreateLogger<CommandPollingWorker>());

        for (var i = 0; i < cycles; i++)
            await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        return new CommandRun(handler.Requests, logs);
    }

    private static JsonObject Command(string id, string? issuedAt)
    {
        var command = new JsonObject { ["commandId"] = id, ["actionType"] = (int)ActionType.ForceMetricSnapshot, ["parameters"] = new JsonObject() };
        if (issuedAt is not null)
            command["issuedAt"] = issuedAt;
        return command;
    }

    private static string At(double secondsFromNow) => AgentNow.AddSeconds(secondsFromNow).UtcDateTime.ToString("O");

    [Theory]
    [InlineData(-3900)] // exactly MaxCommandAgeSeconds + ClockSkewSeconds old: accepted
    [InlineData(0)]
    [InlineData(300)]   // exactly ClockSkewSeconds ahead: accepted
    public async Task FreshCommands_AreAcknowledgedExecutedAndReported(double offsetSeconds)
    {
        var run = await RunCommandLoopAsync([Command("c-fresh", At(offsetSeconds))]);

        run.Acks.Should().ContainSingle();
        var result = JsonNode.Parse(run.Results.Should().ContainSingle().Subject.Body!)!;
        result["success"]!.GetValue<bool>().Should().BeTrue();
        result["message"]!.GetValue<string>().Should().Be("Metric snapshot triggered");
    }

    [Theory]
    [InlineData(-3901, "Rejected: command is stale: issuedAt 2026-11-03T08:54:59.0000000Z is more than 3900s old (max age 3600s + 300s clock skew; agent time 2026-11-03T10:00:00.0000000Z)")]
    [InlineData(301, "Rejected: issuedAt 2026-11-03T10:05:01.0000000Z is more than 300s in the future (agent time 2026-11-03T10:00:00.0000000Z)")]
    public async Task StaleOrFutureCommands_AreReportedAsFailedResults_NeverAcknowledgedOrExecuted(double offsetSeconds, string message)
    {
        var run = await RunCommandLoopAsync([Command("c-rejected", At(offsetSeconds))]);

        run.Acks.Should().BeEmpty("a rejected command is never acknowledged");
        var body = run.Results.Should().ContainSingle().Subject.Body!;
        PortalSpec.Validate("CommandResult", body, SchemaMode.Strict).Should().BeEmpty(body);
        var result = JsonSerializer.Deserialize<CommandResultDto>(body, AgentClientHarness.WireOptions)!;
        result.CommandId.Should().Be("c-rejected");
        result.Success.Should().BeFalse();
        result.Message.Should().Be(message);
    }

    [Fact]
    public async Task UnparseableIssuedAt_IsReportedAsAFailedResult()
    {
        var run = await RunCommandLoopAsync([Command("c-bad", "yesterday")]);

        run.Acks.Should().BeEmpty();
        JsonNode.Parse(run.Results.Should().ContainSingle().Subject.Body!)!["message"]!.GetValue<string>()
            .Should().Be("Rejected: unparseable issuedAt 'yesterday'");
    }

    [Fact]
    public async Task ARejectedCommandDeliveredAgain_IsReportedOnlyOnce()
    {
        var run = await RunCommandLoopAsync([Command("c-stale", At(-7200))], cycles: 3);

        run.Results.Should().ContainSingle("a known commandId is not reported again; the earlier outcome stands");
    }

    [Fact]
    public async Task CommandsWithoutIssuedAt_AreAcceptedWithOneWarning()
    {
        var run = await RunCommandLoopAsync([Command("c-old-portal-1", null), Command("c-old-portal-2", null)]);

        run.Acks.Should().HaveCount(2);
        run.Results.Select(r => JsonNode.Parse(r.Body!)!["success"]!.GetValue<bool>()).Should().Equal(true, true);
        run.Logs.At(LogLevel.Warning).Where(e => e.Message.Contains("issuedAt", StringComparison.Ordinal)).Select(e => e.Message)
            .Should().Equal("Portal sends commands without issuedAt; replay protection is reduced. Update the portal.");
    }

    // ── redirects ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePortalHandler_NeverFollowsRedirects()
    {
        using var handler = PortalTls.CreateHandler(new TlsOptions());

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(304)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task EveryOperation_TreatsA3xxAsFailure(int status)
    {
        var handler = new RecordingHandler(request =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://portal.example.com/new" + request.RequestUri!.AbsolutePath);
            return response;
        });
        var client = AgentClientHarness.CreateClient(AgentClientHarness.Options("https://portal.example.com"), () => handler);
        var ct = TestContext.Current.CancellationToken;

        (await client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), ct)).Should().BeFalse("not delivered: stays in the outbox");
        (await client.SendEventAsync(AgentPayloads.MinimalEvent(), ct)).Should().BeFalse();
        (await client.AcknowledgeCommandAsync(AgentPayloads.Ack("c1"), ct)).Should().BeFalse();
        (await client.ReportCommandResultAsync(AgentPayloads.Result("c1"), ct)).Should().BeFalse("retried on the next poll cycle");
        (await client.AcknowledgeConfigAsync("v1", ct)).Should().BeFalse();
        (await client.PollCommandsAsync(ct)).Should().BeEmpty("a redirected poll means no commands");
        (await client.FetchConfigurationAsync(null, ct)).HasUpdate.Should().BeFalse("a redirected fetch means no change");

        handler.Requests.Should().HaveCount(7, "one request per call: nothing is followed");
    }

    [Fact]
    public async Task ARedirect_IsLoggedOncePer15MinutesPerEndpoint_WithItsAbsoluteTarget()
    {
        var withLocation = true;
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            if (withLocation)
                response.Headers.Location = new Uri("/v2/ingest", UriKind.Relative);
            return response;
        });
        var logs = new RecordingLoggerProvider();
        var time = new FakeTimeProvider(AgentNow);
        var client = AgentClientHarness.CreateClient(AgentClientHarness.Options("https://portal.example.com/wt"), () => handler,
            logs.CreateLogger<HttpsPortalClient>(), time);
        var ct = TestContext.Current.CancellationToken;

        await client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), ct);
        await client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), ct);
        await client.SendEventAsync(AgentPayloads.MinimalEvent(), ct);
        time.Advance(TimeSpan.FromMinutes(15));
        withLocation = false;
        await client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), ct);

        logs.At(LogLevel.Warning).Select(e => e.Message).Should().Equal(
            "Portal redirected api/v1/agent/metrics/batch to https://portal.example.com/v2/ingest; update VisibilityPortal:BaseUrl. " +
            "The redirect (302) was not followed and the call counts as failed (0 more redirect(s) of this endpoint since the last warning)",
            "Portal redirected api/v1/agent/events to https://portal.example.com/v2/ingest; update VisibilityPortal:BaseUrl. " +
            "The redirect (302) was not followed and the call counts as failed (0 more redirect(s) of this endpoint since the last warning)",
            "Portal redirected api/v1/agent/metrics/batch to (no Location header); update VisibilityPortal:BaseUrl. " +
            "The redirect (302) was not followed and the call counts as failed (1 more redirect(s) of this endpoint since the last warning)");
    }

    [Theory]
    [InlineData("https://portal.example.com/wt", "https://portal.example.com/wt/api/v1/agent/events", true)]
    [InlineData("https://portal.example.com/wt/", "https://portal.example.com/wt/api/v1/agent/events", true)]
    [InlineData("https://portal.example.com/wt", "https://portal.example.com/other/api/v1/agent/events", false)]
    [InlineData("https://portal.example.com/wt", "https://portal.example.com/wtx/api/v1/agent/events", false)]
    [InlineData("https://portal.example.com", "http://portal.example.com/api/v1/agent/events", false)]
    [InlineData("https://portal.example.com", "https://portal.example.com:8443/api/v1/agent/events", false)]
    [InlineData("https://portal.example.com", "https://evil.example.com/api/v1/agent/events", false)]
    public void Credentials_AreOnlySentBelowBaseUrl(string baseUrl, string request, bool allowed)
    {
        AgentAuthHandler.IsBelowBaseUrl(new Uri(request), baseUrl).Should().Be(allowed);
    }
}