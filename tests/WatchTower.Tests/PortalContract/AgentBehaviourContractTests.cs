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
/// client and command loop: command freshness (<c>issuedAt</c>) and its rejection result, replay
/// protection by command id, the result retry backoff and give-up, and the
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

    private sealed record CommandRun(IReadOnlyList<RecordedRequest> Requests, RecordingLoggerProvider Logs, IReadOnlyList<TimeSpan> ResultTimes)
    {
        public IReadOnlyList<RecordedRequest> Acks => Requests.Where(r => r.RawPath.EndsWith("/acknowledge", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<RecordedRequest> Results => Requests.Where(r => r.RawPath.EndsWith("/result", StringComparison.Ordinal)).ToList();
    }

    private static bool IsResult(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/result", StringComparison.Ordinal);

    /// <summary>
    /// Runs the real command loop <paramref name="cycles"/> times against a portal returning
    /// <paramref name="pending"/>, <paramref name="step"/> apart on a fake clock starting at
    /// <paramref name="start"/> (default <see cref="AgentNow"/>). Results are answered with
    /// <paramref name="resultStatus"/>; <see cref="CommandRun.ResultTimes"/> says when each was posted.
    /// </summary>
    private async Task<CommandRun> RunCommandLoopAsync(
        JsonArray pending, int cycles = 1, Action<PortalOptions>? configure = null,
        HttpStatusCode resultStatus = HttpStatusCode.NoContent, TimeSpan? step = null, DateTimeOffset? start = null)
    {
        var time = new FakeTimeProvider(start ?? AgentNow);
        var origin = time.GetUtcNow();
        var resultTimes = new List<TimeSpan>();
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return RecordingHandler.Json(pending.ToJsonString());
            if (!IsResult(request))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            lock (resultTimes)
                resultTimes.Add(time.GetUtcNow() - origin);
            return new HttpResponseMessage(resultStatus);
        });
        var options = AgentClientHarness.Options("https://portal.example.com");
        configure?.Invoke(options);
        var logs = new RecordingLoggerProvider();
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
        {
            if (i > 0 && step is { } delay)
                time.Advance(delay);
            await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        }
        return new CommandRun(handler.Requests, logs, resultTimes);
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

    [Theory]
    [InlineData(3600)]
    [InlineData(0)] // applies even with the age check disabled
    public async Task RequireCommandIssuedAt_RejectsACommandWithoutIt(int maxCommandAgeSeconds)
    {
        var run = await RunCommandLoopAsync(
            [Command("c-no-issued-at", null), Command("c-with-issued-at", At(0))], cycles: 3,
            configure: o =>
            {
                o.RequireCommandIssuedAt = true;
                o.CommandPoll.MaxCommandAgeSeconds = maxCommandAgeSeconds;
            });

        run.Acks.Select(a => a.RawPath).Should().AllSatisfy(p => p.Should().Contain("c-with-issued-at"),
            "a rejected command is never acknowledged");
        var results = run.Results.Select(r => r.Body!).ToList();
        results.Should().HaveCount(2, "each command is reported once, however often it is delivered");
        foreach (var body in results)
            PortalSpec.Validate("CommandResult", body, SchemaMode.Strict).Should().BeEmpty(body);

        var rejected = results.Select(b => JsonSerializer.Deserialize<CommandResultDto>(b, AgentClientHarness.WireOptions)!)
            .Should().ContainSingle(r => r.CommandId == "c-no-issued-at").Subject;
        rejected.Success.Should().BeFalse();
        rejected.Message.Should().Be("Rejected: issuedAt is required");
        run.Logs.At(LogLevel.Warning).Select(e => e.Message)
            .Should().NotContain(m => m.StartsWith("Portal sends commands without issuedAt", StringComparison.Ordinal),
                "the command is refused, not accepted with reduced protection");
    }

    [Fact]
    public void ReplayAndResultLimits_HaveTheDocumentedDefaults()
    {
        var options = new PortalOptions();
        options.RequireCommandIssuedAt.Should().BeFalse();
        options.CommandIdRetentionDays.Should().Be(365);
        options.CommandIdMaxCount.Should().Be(200_000);
        options.CommandPoll.RetentionDays.Should().Be(30);
        options.CommandPoll.ResultRetryMaxHours.Should().Be(168);
        CommandPollingWorker.MaxResultsPerCycle.Should().Be(50);
        CommandPollingWorker.DefaultMaxStoredRejections.Should().Be(1000);
        Enumerable.Range(1, 7).Select(SqliteCommandStore.ResultRetryDelay)
            .Should().Equal(new[] { 1, 2, 4, 8, 16, 30, 30 }.Select(m => TimeSpan.FromMinutes(m)).ToList());
    }

    // ── replay protection by commandId ──────────────────────────────────────────────────────

    [Fact]
    public async Task AKnownCommandId_IsAcknowledgedButNeverExecutedAgain_EvenAfterItsRecordIsPurged()
    {
        var first = await RunCommandLoopAsync([Command("c-once", At(0))]);
        first.Results.Should().ContainSingle("the command ran once");

        // CommandPoll:RetentionDays has passed: the finished command and its result are purged.
        using (var store = new SqliteCommandStore(_dbPath))
            (await store.PurgeFinishedCommandsAsync(DateTime.UtcNow.AddDays(1))).Should().Be(1);

        var replay = await RunCommandLoopAsync([Command("c-once", At(0))], cycles: 2);
        replay.Acks.Should().HaveCount(2, "a re-delivery is acknowledged, so the portal can stop sending it");
        replay.Results.Should().BeEmpty("a known commandId is never executed again");
    }

    // ── after an agent restart ──────────────────────────────────────────────────────────────

    private static AgentCommandDto Stored(string id, string? issuedAt) =>
        new(id, (int)ActionType.ForceMetricSnapshot, new Dictionary<string, string>(), null, issuedAt!);

    private static CommandResultDto ResultOf(CommandRun run, string commandId)
    {
        var body = run.Results.Should().ContainSingle(r => r.RawPath.Contains(commandId, StringComparison.Ordinal)).Subject.Body!;
        PortalSpec.Validate("CommandResult", body, SchemaMode.Strict).Should().BeEmpty(body);
        return JsonSerializer.Deserialize<CommandResultDto>(body, AgentClientHarness.WireOptions)!;
    }

    [Fact]
    public async Task ACommandRunningWhenTheAgentStopped_IsReportedInterrupted_AndNotRunAgain()
    {
        // The previous process stored and claimed the command, then stopped before finishing it.
        using (var store = new SqliteCommandStore(_dbPath))
        {
            (await store.SaveCommandAsync(Stored("c-running", At(-60)))).Should().BeTrue();
            (await store.TryBeginExecutionAsync("c-running")).Should().BeTrue();
        }

        var run = await RunCommandLoopAsync([], cycles: 2);

        var result = ResultOf(run, "c-running");
        result.Success.Should().BeFalse();
        result.Message.Should().Be("Interrupted: the agent stopped while this command was running; its outcome is unknown");
    }

    [Fact]
    public async Task ACommandStoredButNotStarted_RunsAfterTheRestart_OrExpires()
    {
        using (var store = new SqliteCommandStore(_dbPath))
        {
            (await store.SaveCommandAsync(Stored("c-recent", At(-60)))).Should().BeTrue();
            (await store.SaveCommandAsync(Stored("c-too-old", At(-3901)))).Should().BeTrue();
        }

        var run = await RunCommandLoopAsync([], cycles: 2);

        ResultOf(run, "c-recent").Should().Match<CommandResultDto>(r => r.Success && r.Message == "Metric snapshot triggered");
        var expired = ResultOf(run, "c-too-old");
        expired.Success.Should().BeFalse();
        expired.Message.Should().Be("Rejected: command expired before it could run (agent restarted)");
    }

    [Fact]
    public async Task AStoredCommandWithoutIssuedAt_IsAgedFromItsReceipt()
    {
        using (var store = new SqliteCommandStore(_dbPath))
            (await store.SaveCommandAsync(Stored("c-no-issued-at", null))).Should().BeTrue();

        // The store dates the receipt with the system clock: restart more than 3900 s later.
        var run = await RunCommandLoopAsync([], start: DateTimeOffset.UtcNow.AddSeconds(3960));

        ResultOf(run, "c-no-issued-at").Message.Should().Be("Rejected: command expired before it could run (agent restarted)");
    }

    // ── result delivery ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusedResult_IsRetriedWithTheDocumentedBackoff()
    {
        // A poll cycle every 30 s for 32 minutes; the portal refuses every result.
        var run = await RunCommandLoopAsync([Command("c-refused", At(0))], cycles: 65,
            resultStatus: HttpStatusCode.InternalServerError, step: TimeSpan.FromSeconds(30));

        run.ResultTimes.Should().Equal(
            new[] { 0, 1, 3, 7, 15, 31 }.Select(m => TimeSpan.FromMinutes(m)).ToList(),
            "attempts follow 1, 2, 4, 8, 16 minutes after the 1st to 5th failure");
        run.Acks.Should().HaveCount(65, "the command is re-delivered every poll and acknowledged each time");
    }

    [Fact]
    public async Task AResultRefusedForResultRetryMaxHours_IsGivenUpWithAWarning()
    {
        // The store dates results with the system clock, so this fake clock starts at the real time.
        var run = await RunCommandLoopAsync([Command("c-abandoned", null)], cycles: 15,
            configure: o => o.CommandPoll.ResultRetryMaxHours = 1,
            resultStatus: HttpStatusCode.InternalServerError, step: TimeSpan.FromMinutes(5), start: DateTimeOffset.UtcNow);

        run.Logs.At(LogLevel.Warning).Select(e => e.Message)
            .Should().ContainSingle(m => m.StartsWith("Gave up delivering the result of command c-abandoned:", StringComparison.Ordinal));
        run.ResultTimes.Should().OnlyContain(t => t <= TimeSpan.FromHours(1), "no attempt is made after giving up");
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