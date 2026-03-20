using FluentAssertions;
using WatchTower.Models;
using WatchTower.Services;

namespace WatchTower.Tests.Services;

public class SqliteMetricsStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMetricsStore _store;

    public SqliteMetricsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_test_{Guid.NewGuid()}.db");
        _store = new SqliteMetricsStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        // SQLite WAL mode creates -wal and -shm files alongside the db
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + ext;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best effort cleanup */ }
            }
        }
    }

    [Fact]
    public void AddAndGetCpuMetrics_ShouldPersist()
    {
        _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 55.5));
        _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 60.0));

        var metrics = _store.GetCpuMetrics(TimeSpan.FromHours(1));
        metrics.Should().HaveCount(2);
        metrics[0].UsagePercent.Should().Be(55.5);
    }

    [Fact]
    public void AddAndGetRamMetrics_ShouldPersist()
    {
        _store.AddRamMetric(new RamMetric(DateTime.UtcNow, 70, 9600, 32000));

        var metrics = _store.GetRamMetrics(TimeSpan.FromHours(1));
        metrics.Should().HaveCount(1);
        metrics[0].AvailableMb.Should().Be(9600);
        metrics[0].TotalMb.Should().Be(32000);
    }

    [Fact]
    public void AddDiskMetric_ShouldUpsertByDrive()
    {
        _store.AddDiskMetric(new DiskMetric(DateTime.UtcNow, "C:\\", 500, 300, 40));
        _store.AddDiskMetric(new DiskMetric(DateTime.UtcNow, "C:\\", 500, 200, 60));

        var metrics = _store.GetLatestDiskMetrics();
        metrics.Should().HaveCount(1);
        metrics[0].UsagePercent.Should().Be(60);
    }

    [Fact]
    public void AddIncident_WithTopProcesses_ShouldPersist()
    {
        var processes = new List<ProcessInfo>
        {
            new("sqlservr", 1234, 500, 2048),
            new("w3wp", 5678, 200, 1024)
        };
        _store.AddIncident(new Incident(DateTime.UtcNow, IncidentType.CpuSpikeDetected, "CPU at 99%", processes));

        var incidents = _store.GetIncidents(TimeSpan.FromHours(1));
        incidents.Should().HaveCount(1);
        incidents[0].TopProcesses.Should().HaveCount(2);
        incidents[0].TopProcesses![0].Name.Should().Be("sqlservr");
    }

    [Fact]
    public void Purge_WithZero_ShouldClearAllData()
    {
        _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 50));
        _store.AddRamMetric(new RamMetric(DateTime.UtcNow, 60, 12800, 32000));
        _store.AddIncident(new Incident(DateTime.UtcNow, IncidentType.CpuSpikeDetected, "test"));

        _store.Purge(TimeSpan.Zero);

        _store.GetCpuMetrics(TimeSpan.FromHours(24)).Should().BeEmpty();
        _store.GetRamMetrics(TimeSpan.FromHours(24)).Should().BeEmpty();
        _store.GetIncidents(TimeSpan.FromHours(24)).Should().BeEmpty();
    }

    [Fact]
    public void DataSurvivesReopen()
    {
        _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 75));
        _store.AddIncident(new Incident(DateTime.UtcNow, IncidentType.ServiceRestarted, "TestService restarted"));

        // Create a new store instance pointing to the same file
        using var store2 = new SqliteMetricsStore(_dbPath);
        store2.GetCpuMetrics(TimeSpan.FromHours(1)).Should().HaveCount(1);
        store2.GetIncidents(TimeSpan.FromHours(1)).Should().HaveCount(1);
    }
}
