using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Tests.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Portal;

/// <summary>
/// Portal calls never follow redirects: a 3xx is a failed call (retried the normal way), it is
/// logged with its target (rate-limited), and nothing, in particular not the bearer token,
/// reaches the redirect target. Runs the real "PortalClient" pipeline against two loopback
/// servers: the portal, which redirects, and the redirect target, on another port (= origin).
/// </summary>
public sealed class PortalRedirectTests : IAsyncLifetime
{
    private const string Token = "secret-agent-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));
    private readonly RecordingLoggerProvider _logs = new();
    private readonly ConcurrentQueue<(string Path, string? Authorization)> _targetHits = new();
    private readonly ConcurrentQueue<string> _portalHits = new();

    private WebApplication _portal = null!;
    private WebApplication _target = null!;
    private string _portalUrl = null!;
    private string _targetUrl = null!;
    private ServiceProvider _services = null!;

    /// <summary>Status the portal answers every request with.</summary>
    private int _status = 302;

    public async ValueTask InitializeAsync()
    {
        _target = await StartServerAsync(context =>
        {
            _targetHits.Enqueue((context.Request.Path.Value!, context.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null));
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        _targetUrl = Address(_target);

        _portal = await StartServerAsync(context =>
        {
            _portalHits.Enqueue(context.Request.Path.Value!);
            context.Response.StatusCode = _status;
            if (_status is >= 300 and <= 399 && _status != 304)
                context.Response.Headers.Location = _targetUrl + context.Request.Path + context.Request.QueryString;
            return Task.CompletedTask;
        });
        _portalUrl = Address(_portal);

        var options = new PortalOptions
        {
            Enabled = true,
            AgentId = "agent-1",
            AgentToken = Token,
            BaseUrl = _portalUrl,
            // The opt-in plain HTTP path; HTTPS uses the very same handler (PortalTls.CreateHandler).
            Tls = new TlsOptions { RequireTls = false },
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IOptionsMonitor<PortalOptions>>(new TestOptionsMonitor<PortalOptions>(options));
        services.AddSingleton<TimeProvider>(_time);
        ServiceCollectionExtensions.AddPortalHttpClient(services, options.BaseUrl);
        services.AddSingleton<HttpsPortalClient>();
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _portal.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static async Task<WebApplication> StartServerAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');

    private HttpsPortalClient Client => _services.GetRequiredService<HttpsPortalClient>();

    private IEnumerable<string> RedirectWarnings =>
        _logs.At(LogLevel.Warning).Select(e => e.Message).Where(m => m.StartsWith("Portal redirected ", StringComparison.Ordinal));

    private static MetricsBatchDto Batch() => new("agent-1", "b-1", "2026-11-04T10:00:00Z", [], [], [], [], [], [], []);

    private static AgentEventDto Event() => new("evt-1", "2026-11-04T10:00:00Z", 4, "disk critical", null);

    public static TheoryData<int, string> RedirectsPerEndpoint()
    {
        var data = new TheoryData<int, string>();
        foreach (var status in new[] { 301, 302, 307, 308 })
        {
            foreach (var endpoint in new[] { "metrics", "events", "acknowledge", "result", "config-acknowledge", "poll", "config" })
                data.Add(status, endpoint);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RedirectsPerEndpoint))]
    public async Task Redirect_IsNotFollowed_CountsAsFailed_AndIsLoggedWithItsTarget(int status, string endpoint)
    {
        _status = status;
        var client = Client;

        var (delivered, path) = endpoint switch
        {
            "metrics" => (await client.SendMetricsBatchAsync(Batch(), Ct), "api/v1/agent/metrics/batch"),
            "events" => (await client.SendEventAsync(Event(), Ct), "api/v1/agent/events"),
            "acknowledge" => (await client.AcknowledgeCommandAsync(new AcknowledgeCommandDto("c-1", "2026-11-04T10:00:00Z"), Ct),
                "api/v1/agent/commands/c-1/acknowledge"),
            "result" => (await client.ReportCommandResultAsync(new CommandResultDto("c-1", true, "ok", "2026-11-04T10:00:00Z"), Ct),
                "api/v1/agent/commands/c-1/result"),
            "config-acknowledge" => (await client.AcknowledgeConfigAsync("v7", Ct), "api/v1/agent/config/acknowledge"),
            "poll" => ((await client.PollCommandsAsync(Ct)).Count > 0, "api/v1/agent/commands/pending"),
            "config" => ((await client.FetchConfigurationAsync("v6", Ct)).HasUpdate, "api/v1/agent/config"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

        delivered.Should().BeFalse("a {0} must never count as delivered", status);
        _portalHits.Should().ContainSingle();
        _targetHits.Should().BeEmpty("redirects are not followed, so nothing (and no token) reaches the target");
        RedirectWarnings.Should().ContainSingle().Which.Should()
            .StartWith($"Portal redirected {path} to {_targetUrl}/{path}")
            .And.Contain("; update VisibilityPortal:BaseUrl")
            .And.Contain($"({status})");
    }

    [Fact]
    public async Task RedirectWarning_IsRateLimitedPerEndpoint_AndCountsTheSuppressedOnes()
    {
        var client = Client;

        for (var i = 0; i < 5; i++)
            (await client.SendMetricsBatchAsync(Batch(), Ct)).Should().BeFalse();
        await client.SendEventAsync(Event(), Ct);

        // One warning per endpoint; the other four metrics redirects are only debug lines.
        RedirectWarnings.Should().HaveCount(2);
        _logs.At(LogLevel.Debug).Count(e => e.Message.StartsWith("Portal redirected api/v1/agent/metrics/batch", StringComparison.Ordinal))
            .Should().Be(4);

        // Result posts of different commands share one endpoint, so one warning for both.
        await client.ReportCommandResultAsync(new CommandResultDto("c-1", true, "ok", "t"), Ct);
        await client.ReportCommandResultAsync(new CommandResultDto("c-2", true, "ok", "t"), Ct);
        RedirectWarnings.Should().HaveCount(3);

        _time.Advance(HttpsPortalClient.RedirectWarningInterval);
        await client.SendMetricsBatchAsync(Batch(), Ct);

        RedirectWarnings.Should().HaveCount(4);
        RedirectWarnings.Last().Should().StartWith("Portal redirected api/v1/agent/metrics/batch")
            .And.Contain("(4 more redirect(s) of this endpoint since the last warning)");
        _targetHits.Should().BeEmpty();
    }

    [Fact]
    public async Task RedirectedOutboxMessages_StayPending_AndAreRetriedWithTheNormalBackoff()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var now = _time.GetUtcNow().UtcDateTime;
        var outbox = new Mock<IOutboxStore>();
        var pending = new List<OutboxMessage>
        {
            new(1, OutboxDispatchWorker.EventMessageType, JsonSerializer.Serialize(Event(), json), 1, 0, CreatedAt: now),
            new(2, OutboxDispatchWorker.MetricsMessageType, JsonSerializer.Serialize(Batch(), json), 0, 0, 10, 20, now),
        };
        outbox.Setup(o => o.GetPendingMessagesAsync(It.IsAny<int>())).ReturnsAsync(() => pending);
        var sync = new Mock<IPortalSyncState>();
        sync.Setup(s => s.GetLastForwardedRowIdAsync(It.IsAny<string>())).ReturnsAsync(0L);
        var metrics = new Mock<IMetricsStore>();
        metrics.Setup(m => m.GetCpuMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((0L, (IReadOnlyList<CpuMetric>)Array.Empty<CpuMetric>()));
        metrics.Setup(m => m.GetRamMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((0L, (IReadOnlyList<RamMetric>)Array.Empty<RamMetric>()));
        var options = _services.GetRequiredService<IOptionsMonitor<PortalOptions>>();
        var worker = new OutboxDispatchWorker(Client, outbox.Object, sync.Object, metrics.Object, new MetricSnapshotTrigger(),
            options, _time, NullLogger<OutboxDispatchWorker>.Instance);

        await worker.RunCycleAsync(Ct);
        pending = [pending[1]];
        await worker.RunCycleAsync(Ct);

        outbox.Verify(o => o.MarkSentAsync(It.IsAny<long>()), Times.Never);
        outbox.Verify(o => o.MarkFailedAsync(1, It.IsAny<string>(), now + OutboxDispatchWorker.EventRetryBaseDelay, int.MaxValue), Times.Once);
        outbox.Verify(o => o.MarkFailedAsync(2, It.IsAny<string>(),
            now.AddSeconds(options.CurrentValue.Outbox.RetryBackoffSeconds), options.CurrentValue.Outbox.MaxRetryAttempts), Times.Once);
        sync.Verify(s => s.SetLastForwardedRowIdAsync("cpu_sent", It.IsAny<long>()), Times.Never);
        _targetHits.Should().BeEmpty();
    }

    [Fact]
    public async Task RedirectedCommandResult_IsNotMarkedSent_AndIsRetriedNextCycle()
    {
        var store = new Mock<ICommandStore>();
        store.Setup(s => s.FailInterruptedCommandsAsync(It.IsAny<string>()))
            .ReturnsAsync(new[] { new CommandResultDto("c-1", false, CommandPollingWorker.InterruptedMessage, "t") });
        store.Setup(s => s.GetDueScheduledCommandsAsync()).ReturnsAsync(Array.Empty<AgentCommandRecord>());
        store.Setup(s => s.GetUnsentResultsAsync()).ReturnsAsync(new[]
        {
            new AgentCommandRecord("c-1", 302, "{}", "Failed", "t", null, "t",
                JsonSerializer.Serialize(new CommandResultDto("c-1", false, CommandPollingWorker.InterruptedMessage, "t"))),
        });
        store.Setup(s => s.PurgeFinishedCommandsAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
        var worker = new CommandPollingWorker(Client, store.Object,
            new ActionHandlerRegistry([], NullLogger<ActionHandlerRegistry>.Instance),
            _services.GetRequiredService<IOptionsMonitor<PortalOptions>>(),
            new TestOptionsMonitor<ServiceMonitorOptions>(new ServiceMonitorOptions()),
            new TestOptionsMonitor<ApplicationPoolMonitorOptions>(new ApplicationPoolMonitorOptions()),
            _time, NullLogger<CommandPollingWorker>.Instance);

        await worker.RunCycleAsync(Ct);
        await worker.RunCycleAsync(Ct);

        store.Verify(s => s.MarkResultSentAsync(It.IsAny<string>()), Times.Never);
        _portalHits.Count(p => p.EndsWith("/result", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(3,
            "the interrupted result is sent once at recovery and again by the retry of every cycle");
        _targetHits.Should().BeEmpty();
    }

    [Fact]
    public async Task Credentials_AreNeverSentOutsideTheConfiguredBaseUrl()
    {
        var http = _services.GetRequiredService<IHttpClientFactory>().CreateClient("PortalClient");

        var send = () => http.GetAsync(_targetUrl + "/api/v1/agent/commands/pending", Ct);

        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Refusing to send the agent credentials*");
        _targetHits.Should().BeEmpty();

        // The configured portal itself does get them.
        _status = 204;
        (await Client.PollCommandsAsync(Ct)).Should().BeEmpty();
        _portalHits.Should().ContainSingle();
    }

    [Theory]
    [InlineData("https://portal.example", "https://portal.example/api/v1/agent/events", true)]
    [InlineData("https://portal.example/", "https://PORTAL.example:443/api/v1/agent/events", true)]
    [InlineData("https://host.example/watchtower", "https://host.example/watchtower/api/v1/agent/events", true)]
    [InlineData("https://host.example/watchtower/", "https://host.example/watchtower", true)]
    [InlineData("https://host.example/watchtower", "https://host.example/watchtowerx/api/v1/agent/events", false)]
    [InlineData("https://host.example/watchtower", "https://host.example/api/v1/agent/events", false)]
    [InlineData("https://portal.example", "http://portal.example/api/v1/agent/events", false)]
    [InlineData("https://portal.example", "https://portal.example:8443/api/v1/agent/events", false)]
    [InlineData("https://portal.example", "https://evil.example/api/v1/agent/events", false)]
    [InlineData("https://portal.example", "https://portal.example.evil.example/api/v1/agent/events", false)]
    [InlineData("", "https://portal.example/api/v1/agent/events", false)]
    public void IsBelowBaseUrl_MatchesSchemeHostPortAndPathPrefix(string baseUrl, string request, bool expected) =>
        AgentAuthHandler.IsBelowBaseUrl(new Uri(request), baseUrl).Should().Be(expected);

    [Fact]
    public void PortalHandler_DoesNotFollowRedirects_ForHttpsHttpAndGrpc()
    {
        // HttpsPortalClient (HTTPS and plain HTTP) and GrpcPortalClient all use this handler.
        using var handler = PortalTls.CreateHandler(new TlsOptions());
        handler.AllowAutoRedirect.Should().BeFalse();

        using var devHandler = PortalTls.CreateHandler(new TlsOptions(), allowUntrustedServerCert: true);
        devHandler.AllowAutoRedirect.Should().BeFalse();
    }
}