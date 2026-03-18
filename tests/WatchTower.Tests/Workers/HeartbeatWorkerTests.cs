using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WatchTower.Configuration;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class HeartbeatWorkerTests
{
    private readonly Mock<IOptionsMonitor<HeartbeatOptions>> _options = new();
    private readonly Mock<ILogger<HeartbeatWorker>> _logger = new();

    [Fact]
    public async Task ExecuteAsync_ShouldPingUrl()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var httpClient = new HttpClient(handler);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Heartbeat")).Returns(httpClient);

        _options.Setup(o => o.CurrentValue).Returns(new HeartbeatOptions
        {
            Url = "https://hc-ping.com/test-uuid",
            IntervalSeconds = 1
        });

        var worker = new HeartbeatWorker(factory.Object, _options.Object, _logger.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        handler.CallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyUrl_ShouldNotPing()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var httpClient = new HttpClient(handler);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Heartbeat")).Returns(httpClient);

        _options.Setup(o => o.CurrentValue).Returns(new HeartbeatOptions
        {
            Url = "",
            IntervalSeconds = 1
        });

        var worker = new HeartbeatWorker(factory.Object, _options.Object, _logger.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        handler.CallCount.Should().Be(0);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int CallCount { get; private set; }

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_response);
        }
    }
}
