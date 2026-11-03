using AwesomeAssertions;

using WatchTower.Models;
using WatchTower.Services;

namespace WatchTower.Tests.Services;

public class MetricsStoreTests
{
    private readonly MetricsStore _store = new();

    [Fact]
    public void AddCpuMetric_ShouldBeRetrievable()
    {
        var metric = new CpuMetric(DateTime.UtcNow, 55.0);
        _store.AddCpuMetric(metric);

        var result = _store.GetCpuMetrics(TimeSpan.FromMinutes(5));
        result.Should().ContainSingle().Which.Should().Be(metric);
    }

    [Fact]
    public void AddRamMetric_ShouldBeRetrievable()
    {
        var metric = new RamMetric(DateTime.UtcNow, 75.0, 8000, 32000);
        _store.AddRamMetric(metric);

        var result = _store.GetRamMetrics(TimeSpan.FromMinutes(5));
        result.Should().ContainSingle().Which.Should().Be(metric);
    }

    [Fact]
    public void AddDiskMetric_ShouldOverwritePreviousForSameDrive()
    {
        var metric1 = new DiskMetric(DateTime.UtcNow, "C:\\", 500, 200, 60);
        var metric2 = new DiskMetric(DateTime.UtcNow, "C:\\", 500, 150, 70);

        _store.AddDiskMetric(metric1);
        _store.AddDiskMetric(metric2);

        var result = _store.GetLatestDiskMetrics();
        result.Should().ContainSingle().Which.UsagePercent.Should().Be(70);
    }

    [Fact]
    public void AddServiceStatus_ShouldOverwritePreviousForSameService()
    {
        var status1 = new ServiceStatus(DateTime.UtcNow, "TestService", "Test Service", "Running", true);
        var status2 = new ServiceStatus(DateTime.UtcNow, "TestService", "Test Service", "Stopped", false);

        _store.AddServiceStatus(status1);
        _store.AddServiceStatus(status2);

        var result = _store.GetLatestServiceStatuses();
        result.Should().ContainSingle().Which.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void AddApplicationPoolStatus_ShouldOverwritePreviousForSamePool()
    {
        var status1 = new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Started", true);
        var status2 = new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Stopped", false);

        _store.AddApplicationPoolStatus(status1);
        _store.AddApplicationPoolStatus(status2);

        var result = _store.GetLatestApplicationPoolStatuses();
        result.Should().ContainSingle().Which.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void AddIncident_ShouldBeRetrievable()
    {
        var incident = new Incident(DateTime.UtcNow, IncidentType.CpuThresholdBreached, "CPU high");
        _store.AddIncident(incident);

        var result = _store.GetIncidents(TimeSpan.FromMinutes(5));
        result.Should().ContainSingle().Which.Should().Be(incident);
    }

    [Fact]
    public void GetCpuMetrics_ShouldFilterByPeriod()
    {
        var old = new CpuMetric(DateTime.UtcNow.AddHours(-3), 50);
        var recent = new CpuMetric(DateTime.UtcNow, 60);

        _store.AddCpuMetric(old);
        _store.AddCpuMetric(recent);

        var result = _store.GetCpuMetrics(TimeSpan.FromHours(1));
        result.Should().ContainSingle().Which.Should().Be(recent);
    }

    [Fact]
    public void Purge_ShouldRemoveOldMetrics()
    {
        var old = new CpuMetric(DateTime.UtcNow.AddHours(-5), 50);
        var recent = new CpuMetric(DateTime.UtcNow, 60);

        _store.AddCpuMetric(old);
        _store.AddCpuMetric(recent);

        _store.Purge(TimeSpan.FromHours(2.5));

        var result = _store.GetCpuMetrics(TimeSpan.FromHours(10));
        result.Should().ContainSingle().Which.Should().Be(recent);
    }

    [Fact]
    public void Purge_ShouldRemoveOldIncidents()
    {
        var old = new Incident(DateTime.UtcNow.AddHours(-5), IncidentType.CpuThresholdBreached, "old");
        var recent = new Incident(DateTime.UtcNow, IncidentType.DiskCritical, "recent");

        _store.AddIncident(old);
        _store.AddIncident(recent);

        _store.Purge(TimeSpan.FromHours(2.5));

        var result = _store.GetIncidents(TimeSpan.FromHours(10));
        result.Should().ContainSingle().Which.Should().Be(recent);
    }
}