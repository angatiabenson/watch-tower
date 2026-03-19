using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly Mock<ILogger<NetworkMonitorWorker>> _logger = new();

    public NetworkMonitorWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new NetworkMonitorOptions
        {
            PollIntervalSeconds = 1,
            OutputQueueWarningThreshold = 0.0
        });

        _reportingOptions.Setup(o => o.CurrentValue).Returns(new ReportingOptions
        {
            EmergencyCooldownSeconds = 300
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStoreNetworkMetrics()
    {
        var metrics = new List<NetworkMetric>
        {
            new(DateTime.UtcNow, "Ethernet", 0)
        };
        _metricsProvider.Setup(m => m.GetNetworkMetrics()).Returns(metrics);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _metricsStore.Verify(s => s.AddNetworkMetric(It.IsAny<NetworkMetric>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_QueueZero_ShouldNotSendEmail()
    {
        var metrics = new List<NetworkMetric>
        {
            new(DateTime.UtcNow, "Ethernet", 0)
        };
        _metricsProvider.Setup(m => m.GetNetworkMetrics()).Returns(metrics);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_QueueAboveThreshold_ShouldSendEmergencyEmail()
    {
        var metrics = new List<NetworkMetric>
        {
            new(DateTime.UtcNow, "Ethernet", 5)
        };
        _metricsProvider.Setup(m => m.GetNetworkMetrics()).Returns(metrics);
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<html/>");

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StartAsync(cts.Token);
        await Task.Delay(1500);
        await worker.StopAsync(CancellationToken.None);

        _emailService.Verify(e => e.SendEmailAsync(
            It.Is<string>(s => s.Contains("Network Saturated")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private NetworkMonitorWorker CreateWorker() => new(
        _metricsProvider.Object,
        _metricsStore.Object,
        _emailService.Object,
        _reportBuilder.Object,
        _options.Object,
        _reportingOptions.Object,
        _logger.Object);
}
