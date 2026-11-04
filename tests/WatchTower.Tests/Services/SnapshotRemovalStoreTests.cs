using AwesomeAssertions;

using WatchTower.Models;
using WatchTower.Services;
using WatchTower.Services.Abstractions;

namespace WatchTower.Tests.Services;

/// <summary>Snapshot-row removal, identical for the SQLite and the in-memory metrics store.</summary>
public sealed class SnapshotRemovalStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchtower_snaprm_{Guid.NewGuid()}.db");
    private SqliteMetricsStore? _sqlite;

    public void Dispose()
    {
        _sqlite?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + ext); } catch (IOException) { /* best effort */ }
        }
    }

    private IMetricsStore Store(string kind) => kind == "sqlite" ? _sqlite = new SqliteMetricsStore(_dbPath) : new MetricsStore();

    private static readonly DateTime T = DateTime.Parse("2026-11-04T00:00:00Z").ToUniversalTime();

    /// <summary>Adds a row named <paramref name="name"/> of every snapshot kind.</summary>
    private static void AddAll(IMetricsStore store, string name)
    {
        store.AddDiskMetric(new DiskMetric(T, name, 100, 50, 50));
        store.AddDiskQueueMetric(new DiskQueueMetric(T, name, 0));
        store.AddNetworkMetric(new NetworkMetric(T, name, 0));
        store.AddServiceStatus(new ServiceStatus(T, name, name, "Running", true));
        store.AddApplicationPoolStatus(new ApplicationPoolStatus(T, name, "Started", true));
        store.AddEndpointStatus(new EndpointStatus(T, name, "Http", "https://x", "Up", true));
    }

    private static IReadOnlyList<string> Latest(IMetricsStore store, SnapshotKind kind) => kind switch
    {
        SnapshotKind.Disk => store.GetLatestDiskMetrics().Select(m => m.DriveName).ToList(),
        SnapshotKind.DiskQueue => store.GetLatestDiskQueueMetrics().Select(m => m.InstanceName).ToList(),
        SnapshotKind.Network => store.GetLatestNetworkMetrics().Select(m => m.InterfaceName).ToList(),
        SnapshotKind.Service => store.GetLatestServiceStatuses().Select(m => m.ServiceName).ToList(),
        SnapshotKind.ApplicationPool => store.GetLatestApplicationPoolStatuses().Select(m => m.ApplicationPoolName).ToList(),
        SnapshotKind.Endpoint => store.GetLatestEndpointStatuses().Select(m => m.Name).ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static TheoryData<string, SnapshotKind> StoresAndKinds()
    {
        var data = new TheoryData<string, SnapshotKind>();
        foreach (var store in new[] { "sqlite", "memory" })
        {
            foreach (var kind in Enum.GetValues<SnapshotKind>())
                data.Add(store, kind);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(StoresAndKinds))]
    public void RemoveSnapshots_DeletesOnlyThatKindsNamedRows_CaseInsensitively(string storeKind, SnapshotKind kind)
    {
        var store = Store(storeKind);
        AddAll(store, "Alpha");
        AddAll(store, "Beta");

        store.RemoveSnapshots(kind, ["ALPHA", "missing"]).Should().Be(1);

        store.GetSnapshotNames(kind).Should().BeEquivalentTo(["Beta"]);
        Latest(store, kind).Should().BeEquivalentTo(["Beta"]);
        foreach (var other in Enum.GetValues<SnapshotKind>().Where(k => k != kind))
            store.GetSnapshotNames(other).Should().BeEquivalentTo(["Alpha", "Beta"], "only {0} rows were removed", kind);

        store.RemoveSnapshots(kind, []).Should().Be(0);
        AddAll(store, "Alpha");
        Latest(store, kind).Should().BeEquivalentTo(["Alpha", "Beta"], "a resource read again is reported again");
    }

    [Theory]
    [MemberData(nameof(StoresAndKinds))]
    public void RemoveSnapshotsExcept_KeepsTheConfiguredNames_RenamingCaseOnlyChanges(string storeKind, SnapshotKind kind)
    {
        var store = Store(storeKind);
        AddAll(store, "Alpha");
        AddAll(store, "Beta");
        AddAll(store, "Gamma");

        store.RemoveSnapshotsExcept(kind, ["Alpha", "gamma", "Delta"]).Should().BeEquivalentTo(["Beta"]);

        Latest(store, kind).Should().BeEquivalentTo(["Alpha", "gamma"], "a case-only rename keeps the row under the new spelling");
        store.GetSnapshotNames(kind).Should().BeEquivalentTo(["Alpha", "gamma"]);
        store.GetSnapshotNames(kind == SnapshotKind.Disk ? SnapshotKind.Network : SnapshotKind.Disk)
            .Should().BeEquivalentTo(["Alpha", "Beta", "Gamma"], "other kinds are untouched");

        AddAll(store, "gamma");
        Latest(store, kind).Should().BeEquivalentTo(["Alpha", "gamma"], "the next reading replaces the renamed row");

        store.RemoveSnapshotsExcept(kind, []).Should().BeEquivalentTo(["Alpha", "gamma"]);
        Latest(store, kind).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(StoresAndKinds))]
    public void RemoveSnapshotsExcept_KeepsOneRowPerName_PreferringTheConfiguredSpelling(string storeKind, SnapshotKind kind)
    {
        var store = Store(storeKind);
        AddAll(store, "SITE");
        AddAll(store, "Site");
        AddAll(store, "API");
        AddAll(store, "Api");
        AddAll(store, "api");

        var removed = store.RemoveSnapshotsExcept(kind, ["site", "api", "Api"]);

        removed.Should().HaveCount(3);
        removed.Should().Contain(["API", "Api"], "an exact row of the first configured spelling exists");
        Latest(store, kind).Should().BeEquivalentTo(["site", "api"]);
    }
}