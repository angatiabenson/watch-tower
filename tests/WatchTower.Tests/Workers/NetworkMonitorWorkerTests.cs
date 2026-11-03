using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class NetworkMonitorWorkerTests
{
    private readonly Mock<ISystemMetricsProvider> _metricsProvider = new();
    private readonly Mock<IMetricsStore> _metricsStore = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly Mock<IOptionsMonitor<NetworkMonitorOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<ReportingOptions>> _reportingOptions = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<ILogger<NetworkMonitorWorker>> _logger = new();

    public NetworkMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new NetworkMonitorOptions
        {
            PollIntervalSeconds = 1,
            OutputQueueWarningThreshold = 0.0
        });
        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions { EmergencyCooldownSeconds = 300 });
        _emailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");
    }

    [Fact]
    public async Task PollOnce_ShouldStoreNetworkMetrics()
    {
        _metricsProvider.Setup(m => m.GetNetworkMetrics()).Returns(new List<NetworkMetric> { new(_time.GetUtcNow().UtcDateTime, "Ethernet", 0) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddNetworkMetric(It.IsAny<NetworkMetric>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_QueueZero_ShouldNotSendEmail()
    {
        _metricsProvider.Setup(m => m.GetNetworkMetrics()).Returns(new List<NetworkMetric> { new(_time.GetUtcNow().UtcDateTime, "Ethernet", 0) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_QueueAboveThreshold_ShouldSendEmergencyEmail()
    {
        _metricsProvider.Setup(m => m.GetNetworkMetrics()).Returns(new List<NetworkMetric> { new(_time.GetUtcNow().UtcDateTime, "Ethernet", 5) });

        await CreateWorker().PollOnceAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Network Saturated")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private NetworkMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        new WatchTower.Portal.NullPortalEventPublisher(),
        _options.Object,
        _reportingOptions.Object,
        _time,
        _logger.Object);
}