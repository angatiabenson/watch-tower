using AwesomeAssertions;

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
    public void PurgeReportedData_PreservesUnacknowledgedRows()
    {
        for (var i = 0; i < 5; i++)
            _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, i));

        // The portal has only confirmed delivery up to rowid 3.
        var (lastRowId, all) = _store.GetCpuMetricsSince(0, 100);
        all.Should().HaveCount(5);
        lastRowId.Should().Be(5);

        var result = _store.PurgeReportedData(cpuForwardedRowId: 3, ramForwardedRowId: 0, incidentsUpToUtc: DateTime.UtcNow);

        // Rows 1-3 (acknowledged) removed; rows 4-5 (un-acked) preserved -> no data loss.
        var (_, remaining) = _store.GetCpuMetricsSince(0, 100);
        remaining.Should().HaveCount(2);
        result.CpuTableEmptied.Should().BeFalse();
    }

    [Fact]
    public void PurgeReportedData_FullForward_EmptiesTable()
    {
        _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 1));
        _store.AddCpuMetric(new CpuMetric(DateTime.UtcNow, 2));

        var result = _store.PurgeReportedData(cpuForwardedRowId: long.MaxValue, ramForwardedRowId: long.MaxValue, incidentsUpToUtc: DateTime.UtcNow);

        _store.GetCpuMetrics(TimeSpan.FromHours(1)).Should().BeEmpty();
        result.CpuTableEmptied.Should().BeTrue();
        result.RamTableEmptied.Should().BeTrue();
    }

    [Fact]
    public void PurgeReportedData_KeepsIncidentsRecordedAfterTheReportSnapshot()
    {
        var snapshot = DateTime.UtcNow;
        _store.AddIncident(new Incident(snapshot.AddMinutes(-10), IncidentType.DiskCritical, "reported"));
        _store.AddIncident(new Incident(snapshot, IncidentType.ServiceDown, "reported at cutoff"));
        _store.AddIncident(new Incident(snapshot.AddSeconds(5), IncidentType.CpuSpikeDetected, "raised while sending"));

        _store.PurgeReportedData(long.MaxValue, long.MaxValue, incidentsUpToUtc: snapshot);

        // Previously every incident was deleted, losing the one raised after the report was built.
        _store.GetIncidents(TimeSpan.FromHours(1)).Select(i => i.Description).Should().Equal("raised while sending");
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
            new("sqlservr", 1234, 500, 2048, 42.5),
            new("w3wp", 5678, 200, 1024, 25.0)
        };
        _store.AddIncident(new Incident(DateTime.UtcNow, IncidentType.CpuSpikeDetected, "CPU at 99%", processes));

        var incidents = _store.GetIncidents(TimeSpan.FromHours(1));
        incidents.Should().HaveCount(1);
        incidents[0].TopProcesses.Should().HaveCount(2);
        incidents[0].TopProcesses![0].Name.Should().Be("sqlservr");
        incidents[0].TopProcesses![0].CpuPercent.Should().Be(42.5);
    }

    [Fact]
    public void AddApplicationPoolStatus_ShouldPersist()
    {
        _store.AddApplicationPoolStatus(
            new ApplicationPoolStatus(DateTime.UtcNow, "DefaultAppPool", "Started", true)
        );

        var statuses = _store.GetLatestApplicationPoolStatuses();
        statuses.Should().ContainSingle();
        statuses[0].ApplicationPoolName.Should().Be("DefaultAppPool");
        statuses[0].IsRunning.Should().BeTrue();
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