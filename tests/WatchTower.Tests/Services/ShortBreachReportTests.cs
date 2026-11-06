using AwesomeAssertions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Alerting;

namespace WatchTower.Tests.Services;

/// <summary>The daily report's summary of breaches that cleared within their waiting period.</summary>
public class ShortBreachReportTests
{
    private readonly PendingAlertTracker _tracker = new();

    private ReportBuilder Builder()
    {
        var store = new Mock<IMetricsStore>();
        store.Setup(s => s.GetCpuMetrics(It.IsAny<TimeSpan>())).Returns([]);
        store.Setup(s => s.GetRamMetrics(It.IsAny<TimeSpan>())).Returns([]);
        store.Setup(s => s.GetLatestDiskMetrics()).Returns([]);
        store.Setup(s => s.GetLatestDiskQueueMetrics()).Returns([]);
        store.Setup(s => s.GetLatestNetworkMetrics()).Returns([]);
        store.Setup(s => s.GetLatestServiceStatuses()).Returns([]);
        store.Setup(s => s.GetLatestApplicationPoolStatuses()).Returns([]);
        store.Setup(s => s.GetIncidents(It.IsAny<TimeSpan>())).Returns([]);
        return new ReportBuilder(store.Object, new TestOptionsMonitor<DisplayOptions>(new DisplayOptions()), _tracker);
    }

    private void ShortBreach(AlertKey key, DateTime start, TimeSpan lasted)
    {
        _tracker.Observe(key, AlertSeverity.Warning, TimeSpan.FromHours(1), "test", start, openSeverity: null);
        _tracker.Clear(key, start + lasted);
    }

    [Fact]
    public void Report_CountsBreachesThatRecoveredBeforeAlerting_PerKind_WithTheLongest()
    {
        var now = DateTime.UtcNow;
        ShortBreach(AlertKey.DiskQueue("0 C:"), now.AddHours(-3), TimeSpan.FromSeconds(40));
        ShortBreach(AlertKey.DiskQueue("1 D:"), now.AddHours(-2), TimeSpan.FromMinutes(3));
        ShortBreach(AlertKey.Service("Spooler"), now.AddHours(-1), TimeSpan.FromSeconds(15));
        ShortBreach(AlertKey.Cpu, now.AddHours(-30), TimeSpan.FromMinutes(2)); // before this report's period

        var builder = Builder();
        var report = builder.BuildReport(TimeSpan.FromHours(24));

        report.ShortBreaches.Should().Equal(
            new ShortBreachSummary("diskqueue", "Disk queue", 2, TimeSpan.FromMinutes(3)),
            new ShortBreachSummary("svc", "Service down", 1, TimeSpan.FromSeconds(15)));

        var html = builder.RenderScheduledReportHtml(report);
        html.Should().Contain("Recovered before alerting")
            .And.Contain("Disk queue").And.Contain("2&times;, longest 3 min")
            .And.Contain("Service down").And.Contain("1&times;, longest 15s")
            .And.Contain("nobody was notified");
    }

    [Fact]
    public void Report_WithoutShortBreaches_HasNoSuchSection()
    {
        var builder = Builder();

        builder.RenderScheduledReportHtml(builder.BuildReport(TimeSpan.FromHours(24)))
            .Should().NotContain("Recovered before alerting");
    }
}