using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Tests.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class HeartbeatWorkerTests
{
    private readonly Mock<IOptionsMonitor<HeartbeatOptions>> _options = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<ILogger<HeartbeatWorker>> _logger = new();
    private readonly WorkerHealthRegistry _health;

    public HeartbeatWorkerTests()
    {
        _health = new WorkerHealthRegistry(_time);
        _options.Setup(o => o.CurrentValue).Returns(new HeartbeatOptions { Url = "https://hc-ping.com/test-uuid", IntervalSeconds = 1 });
    }

    private HeartbeatWorker CreateWorker(MockHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Heartbeat")).Returns(new HttpClient(handler));
        return new HeartbeatWorker(factory.Object, _options.Object, _health, _time, _logger.Object);
    }

    [Fact]
    public async Task PingOnce_ShouldPingConfiguredUrl()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        await CreateWorker(handler).PingOnceAsync(CancellationToken.None);

        handler.CallCount.Should().Be(1);
        handler.Requests.Single().Should().Be((HttpMethod.Get, "https://hc-ping.com/test-uuid", (string?)null));
    }

    [Fact]
    public async Task PingOnce_EmptyUrl_ShouldNotPing()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        _options.Setup(o => o.CurrentValue).Returns(new HeartbeatOptions { Url = "", IntervalSeconds = 1 });

        await CreateWorker(handler).PingOnceAsync(CancellationToken.None);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PingOnce_FailedRequest_RetriesOnceAndDoesNotThrow()
    {
        // First call throws, retry (after the back-off) succeeds. Drive the back-off with
        // fake time so the test does not wait real seconds.
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK))
        {
            ThrowOnFirstCall = true
        };

        var task = CreateWorker(handler).PingOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5)); // release the retry back-off delay
        await task;

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task PingOnce_HealthyWorkers_PingSuccess()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        _health.ReportSuccess(WorkerNames.Cpu, TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(119)); // under the 2-minute floor

        await CreateWorker(handler).PingOnceAsync(CancellationToken.None);

        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task PingOnce_StaleWorker_PingsFailWithReason()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        _health.ReportSuccess(WorkerNames.Disk, TimeSpan.FromSeconds(60));
        _health.ReportSuccess(WorkerNames.Cpu, TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(181)); // > 3 x 60 s for disk
        _health.ReportSuccess(WorkerNames.Cpu, TimeSpan.FromSeconds(5)); // cpu still cycling

        await CreateWorker(handler).PingOnceAsync(CancellationToken.None);

        var (method, url, body) = handler.Requests.Single();
        method.Should().Be(HttpMethod.Post);
        url.Should().Be("https://hc-ping.com/test-uuid/fail");
        body.Should().Contain("disk").And.Contain("3 min").And.NotContain("cpu");
    }

    [Theory]
    [InlineData("https://hc-ping.com/uuid", "https://hc-ping.com/uuid/fail")]
    [InlineData("https://hc-ping.com/uuid/", "https://hc-ping.com/uuid/fail")]
    [InlineData("https://hc-ping.com/key/slug?create=1", "https://hc-ping.com/key/slug/fail?create=1")]
    public void FailUrl_AppendsFailToThePath(string url, string expected) =>
        HeartbeatWorker.FailUrl(url).Should().Be(expected);

    [Fact]
    public void Registry_StaleAfterIsThreeIntervalsWithATwoMinuteFloor()
    {
        WorkerHealthRegistry.StaleAfter(TimeSpan.FromSeconds(5)).Should().Be(TimeSpan.FromMinutes(2));
        WorkerHealthRegistry.StaleAfter(TimeSpan.FromSeconds(60)).Should().Be(TimeSpan.FromMinutes(3));

        _health.ReportSuccess(WorkerNames.Network, TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromMinutes(2));
        _health.GetStale().Should().BeEmpty();
        _time.Advance(TimeSpan.FromSeconds(1));
        _health.GetStale().Should().ContainSingle().Which.Worker.Should().Be(WorkerNames.Network);
    }

    [Fact]
    public async Task MonitorWorker_ReportsLivenessFromItsLoop()
    {
        var provider = new Mock<ISystemMetricsProvider>();
        provider.Setup(p => p.GetCpuUsage()).Returns(10f);
        var store = new Mock<IMetricsStore>();
        var options = new Mock<IOptionsMonitor<CpuMonitorOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new CpuMonitorOptions { PollIntervalSeconds = 5, ThresholdPercent = 99 });
        var worker = new CpuMonitorWorker(provider.Object, store.Object,
            TestAlerts.Create(store.Object, _time, new RecordingDispatcher()), new WatchTower.Portal.NullPortalEventPublisher(),
            options.Object, _time, NullLogger<CpuMonitorWorker>.Instance, _health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!store.Invocations.Any(i => i.Method.Name == nameof(IMetricsStore.AddCpuMetric)) && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            _health.Snapshot().Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                Worker = WorkerNames.Cpu,
                Interval = TimeSpan.FromSeconds(5),
                IsStale = false,
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int CallCount { get; private set; }
        public bool ThrowOnFirstCall { get; init; }
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        public MockHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (ThrowOnFirstCall && CallCount == 1)
                throw new HttpRequestException("simulated failure");
            return _response;
        }
    }
}