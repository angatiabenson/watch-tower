using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class HeartbeatWorkerTests
{
    private readonly Mock<IOptionsMonitor<HeartbeatOptions>> _options = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<ILogger<HeartbeatWorker>> _logger = new();

    private HeartbeatWorker CreateWorker(MockHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Heartbeat")).Returns(new HttpClient(handler));
        return new HeartbeatWorker(factory.Object, _options.Object, _time, _logger.Object);
    }

    [Fact]
    public async Task PingOnce_ShouldPingConfiguredUrl()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        _options.Setup(o => o.CurrentValue).Returns(new HeartbeatOptions { Url = "https://hc-ping.com/test-uuid", IntervalSeconds = 1 });

        await CreateWorker(handler).PingOnceAsync(CancellationToken.None);

        handler.CallCount.Should().Be(1);
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
        _options.Setup(o => o.CurrentValue).Returns(new HeartbeatOptions { Url = "https://hc-ping.com/test-uuid", IntervalSeconds = 1 });

        var task = CreateWorker(handler).PingOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5)); // release the retry back-off delay
        await task;

        handler.CallCount.Should().Be(2);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int CallCount { get; private set; }
        public bool ThrowOnFirstCall { get; init; }

        public MockHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnFirstCall && CallCount == 1)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("simulated failure"));
            return Task.FromResult(_response);
        }
    }
}