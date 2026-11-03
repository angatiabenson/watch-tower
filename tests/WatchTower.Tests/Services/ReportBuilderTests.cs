using FluentAssertions;

using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services;
using WatchTower.Services.Abstractions;

namespace WatchTower.Tests.Services;

public class ReportBuilderTests
{
    private readonly Mock<IMetricsStore> _storeMock = new();
    private readonly Mock<IOptionsMonitor<DisplayOptions>> _displayOptions = new();
    private readonly ReportBuilder _builder;

    public ReportBuilderTests()
    {
        _displayOptions.Setup(o => o.CurrentValue).Returns(new DisplayOptions());

        // Default empty returns for all store methods
        _storeMock.Setup(s => s.GetCpuMetrics(It.IsAny<TimeSpan>())).Returns(new List<CpuMetric>());
        _storeMock.Setup(s => s.GetRamMetrics(It.IsAny<TimeSpan>())).Returns(new List<RamMetric>());
        _storeMock.Setup(s => s.GetLatestDiskMetrics()).Returns(new List<DiskMetric>());
        _storeMock.Setup(s => s.GetLatestDiskQueueMetrics()).Returns(new List<DiskQueueMetric>());
        _storeMock.Setup(s => s.GetLatestNetworkMetrics()).Returns(new List<NetworkMetric>());
        _storeMock.Setup(s => s.GetLatestServiceStatuses()).Returns(new List<ServiceStatus>());
        _storeMock.Setup(s => s.GetLatestApplicationPoolStatuses()).Returns(new List<ApplicationPoolStatus>());
        _storeMock.Setup(s => s.GetIncidents(It.IsAny<TimeSpan>())).Returns(new List<Incident>());

        _builder = new ReportBuilder(_storeMock.Object, _displayOptions.Object);
    }

    [Fact]
    public void BuildReport_ShouldComputeMinAvgMaxForCpu()
    {
        var now = DateTime.UtcNow;
        var cpuMetrics = new List<CpuMetric>
        {
            new(now.AddMinutes(-10), 30),
            new(now.AddMinutes(-5), 60),
            new(now, 90)
        };

        _storeMock.Setup(s => s.GetCpuMetrics(It.IsAny<TimeSpan>())).Returns(cpuMetrics);

        var report = _builder.BuildReport(TimeSpan.FromHours(2));

        report.CpuMin.Should().Be(30);
        report.CpuAvg.Should().Be(60);
        report.CpuMax.Should().Be(90);
    }

    [Fact]
    public void BuildReport_ShouldComputeMinAvgMaxForRam()
    {
        var now = DateTime.UtcNow;
        var ramMetrics = new List<RamMetric>
        {
            new(now.AddMinutes(-10), 40, 19200, 32000),
            new(now.AddMinutes(-5), 60, 12800, 32000),
            new(now, 80, 6400, 32000)
        };

        _storeMock.Setup(s => s.GetRamMetrics(It.IsAny<TimeSpan>())).Returns(ramMetrics);

        var report = _builder.BuildReport(TimeSpan.FromHours(2));

        report.RamMin.Should().Be(40);
        report.RamAvg.Should().Be(60);
        report.RamMax.Should().Be(80);
        report.RamTotalGb.Should().BeApproximately(32000.0 / 1024, 0.1);
        report.RamUsedGb.Should().BeApproximately((32000.0 - 6400) / 1024, 0.1);
    }

    [Fact]
    public void BuildReport_WithNoMetrics_ShouldReturnZeros()
    {
        var report = _builder.BuildReport(TimeSpan.FromHours(2));

        report.CpuMin.Should().Be(0);
        report.CpuAvg.Should().Be(0);
        report.CpuMax.Should().Be(0);
        report.RamMin.Should().Be(0);
        report.RamAvg.Should().Be(0);
        report.RamMax.Should().Be(0);
    }

    [Fact]
    public void RenderScheduledReportHtml_ShouldContainReportTitle()
    {
        var report = new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(2),
            CpuAvg = 50
        };

        var html = _builder.RenderScheduledReportHtml(report);

        html.Should().Contain("WatchTower");
        html.Should().Contain("50%");
    }

    [Fact]
    public void RenderEmergencyReportHtml_ShouldContainEmergencyBanner()
    {
        var report = new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(2)
        };

        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.CpuThresholdBreached,
            "CPU at 99.5%",
            [new ProcessInfo("sqlservr", 1234, 500, 2048, 42.5)]
        );

        var html = _builder.RenderEmergencyReportHtml(report, incident);

        html.Should().Contain("EMERGENCY");
        html.Should().Contain("CPU Threshold Breached");
        html.Should().Contain("CPU at 99.5%");
        html.Should().Contain("CPU %");
        html.Should().Contain("42.5%");
        html.Should().NotContain("CPU TIME");
        html.Should().Contain("EAT");
    }

    [Fact]
    public void RenderEmergencyReportHtml_ShouldHtmlEncodeDynamicValues()
    {
        var report = new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(2)
        };

        // A malicious/odd process name and incident description must not appear raw in the
        // HTML body (markup injection); they should be HTML-encoded.
        var incident = new Incident(
            DateTime.UtcNow,
            IncidentType.CpuThresholdBreached,
            "CPU at 99.5% <script>alert(1)</script>",
            [new ProcessInfo("<img src=x onerror=alert(1)>", 1234, 500, 2048, 42.5)]
        );

        var html = _builder.RenderEmergencyReportHtml(report, incident);

        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().NotContain("<img src=x onerror=alert(1)>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
    }

    [Fact]
    public void RenderScheduledReportHtml_ShouldContainApplicationPoolSection()
    {
        var report = new ServerReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportPeriod = TimeSpan.FromHours(2),
            ApplicationPoolSnapshots =
            [
                new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Started", true)
            ]
        };

        var html = _builder.RenderScheduledReportHtml(report);

        html.Should().Contain("Watched IIS Application Pools");
        html.Should().Contain("DefaultAppPool");
    }
}