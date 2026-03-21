using System.Text.Json;
using Microsoft.Data.Sqlite;
using WatchTower.Models;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class SqliteMetricsStore : IMetricsStore, IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public SqliteMetricsStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS cpu_metrics (
                timestamp TEXT NOT NULL,
                usage_percent REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ram_metrics (
                timestamp TEXT NOT NULL,
                usage_percent REAL NOT NULL,
                available_mb REAL NOT NULL,
                total_mb REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS disk_metrics (
                drive_name TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                total_gb REAL NOT NULL,
                free_gb REAL NOT NULL,
                usage_percent REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS disk_queue_metrics (
                instance_name TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                queue_length REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS network_metrics (
                interface_name TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                output_queue_length REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS service_statuses (
                service_name TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                display_name TEXT NOT NULL,
                status TEXT NOT NULL,
                is_running INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS incidents (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                type INTEGER NOT NULL,
                description TEXT NOT NULL,
                top_processes_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_cpu_timestamp ON cpu_metrics(timestamp);
            CREATE INDEX IF NOT EXISTS idx_ram_timestamp ON ram_metrics(timestamp);
            CREATE INDEX IF NOT EXISTS idx_incidents_timestamp ON incidents(timestamp);
        ";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        return conn;
    }

    public void AddCpuMetric(CpuMetric metric)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO cpu_metrics (timestamp, usage_percent) VALUES (@ts, @usage)";
            cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@usage", metric.UsagePercent);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddRamMetric(RamMetric metric)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO ram_metrics (timestamp, usage_percent, available_mb, total_mb) VALUES (@ts, @usage, @avail, @total)";
            cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@usage", metric.UsagePercent);
            cmd.Parameters.AddWithValue("@avail", metric.AvailableMb);
            cmd.Parameters.AddWithValue("@total", metric.TotalMb);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddDiskMetric(DiskMetric metric)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR REPLACE INTO disk_metrics (drive_name, timestamp, total_gb, free_gb, usage_percent)
                                VALUES (@drive, @ts, @total, @free, @usage)";
            cmd.Parameters.AddWithValue("@drive", metric.DriveName);
            cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@total", metric.TotalGb);
            cmd.Parameters.AddWithValue("@free", metric.FreeGb);
            cmd.Parameters.AddWithValue("@usage", metric.UsagePercent);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddDiskQueueMetric(DiskQueueMetric metric)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR REPLACE INTO disk_queue_metrics (instance_name, timestamp, queue_length)
                                VALUES (@name, @ts, @queue)";
            cmd.Parameters.AddWithValue("@name", metric.InstanceName);
            cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@queue", metric.QueueLength);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddNetworkMetric(NetworkMetric metric)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR REPLACE INTO network_metrics (interface_name, timestamp, output_queue_length)
                                VALUES (@name, @ts, @queue)";
            cmd.Parameters.AddWithValue("@name", metric.InterfaceName);
            cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@queue", metric.OutputQueueLength);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddServiceStatus(ServiceStatus status)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR REPLACE INTO service_statuses (service_name, timestamp, display_name, status, is_running)
                                VALUES (@name, @ts, @display, @status, @running)";
            cmd.Parameters.AddWithValue("@name", status.ServiceName);
            cmd.Parameters.AddWithValue("@ts", status.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@display", status.DisplayName);
            cmd.Parameters.AddWithValue("@status", status.Status);
            cmd.Parameters.AddWithValue("@running", status.IsRunning ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddIncident(Incident incident)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO incidents (timestamp, type, description, top_processes_json)
                                VALUES (@ts, @type, @desc, @procs)";
            cmd.Parameters.AddWithValue("@ts", incident.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@type", (int)incident.Type);
            cmd.Parameters.AddWithValue("@desc", incident.Description);
            cmd.Parameters.AddWithValue(
                "@procs",
                incident.TopProcesses is { Count: > 0 }
                    ? JsonSerializer.Serialize(incident.TopProcesses)
                    : (object)DBNull.Value
            );
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CpuMetric> GetCpuMetrics(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var results = new List<CpuMetric>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT timestamp, usage_percent FROM cpu_metrics WHERE timestamp >= @cutoff ORDER BY timestamp";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new CpuMetric(
                        DateTime.Parse(reader.GetString(0)).ToUniversalTime(),
                        reader.GetDouble(1)
                    )
                );
            }
        }
        return results;
    }

    public IReadOnlyList<RamMetric> GetRamMetrics(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var results = new List<RamMetric>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT timestamp, usage_percent, available_mb, total_mb FROM ram_metrics WHERE timestamp >= @cutoff ORDER BY timestamp";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new RamMetric(
                        DateTime.Parse(reader.GetString(0)).ToUniversalTime(),
                        reader.GetDouble(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3)
                    )
                );
            }
        }
        return results;
    }

    public IReadOnlyList<DiskMetric> GetLatestDiskMetrics()
    {
        var results = new List<DiskMetric>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT drive_name, timestamp, total_gb, free_gb, usage_percent FROM disk_metrics";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new DiskMetric(
                        DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                        reader.GetString(0),
                        reader.GetDouble(2),
                        reader.GetDouble(3),
                        reader.GetDouble(4)
                    )
                );
            }
        }
        return results;
    }

    public IReadOnlyList<DiskQueueMetric> GetLatestDiskQueueMetrics()
    {
        var results = new List<DiskQueueMetric>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT instance_name, timestamp, queue_length FROM disk_queue_metrics";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new DiskQueueMetric(
                        DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                        reader.GetString(0),
                        reader.GetDouble(2)
                    )
                );
            }
        }
        return results;
    }

    public IReadOnlyList<NetworkMetric> GetLatestNetworkMetrics()
    {
        var results = new List<NetworkMetric>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT interface_name, timestamp, output_queue_length FROM network_metrics";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new NetworkMetric(
                        DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                        reader.GetString(0),
                        reader.GetDouble(2)
                    )
                );
            }
        }
        return results;
    }

    public IReadOnlyList<ServiceStatus> GetLatestServiceStatuses()
    {
        var results = new List<ServiceStatus>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT service_name, timestamp, display_name, status, is_running FROM service_statuses";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(
                    new ServiceStatus(
                        DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                        reader.GetString(0),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt32(4) == 1
                    )
                );
            }
        }
        return results;
    }

    public IReadOnlyList<Incident> GetIncidents(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var results = new List<Incident>();

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT timestamp, type, description, top_processes_json FROM incidents WHERE timestamp >= @cutoff ORDER BY timestamp";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                List<ProcessInfo>? topProcesses = null;
                if (!reader.IsDBNull(3))
                {
                    topProcesses = JsonSerializer.Deserialize<List<ProcessInfo>>(
                        reader.GetString(3)
                    );
                }

                results.Add(
                    new Incident(
                        DateTime.Parse(reader.GetString(0)).ToUniversalTime(),
                        (IncidentType)reader.GetInt32(1),
                        reader.GetString(2),
                        topProcesses
                    )
                );
            }
        }
        return results;
    }

    public void Purge(TimeSpan maxAge)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            if (maxAge == TimeSpan.Zero)
            {
                // Full purge — clear all data after daily report sent
                cmd.CommandText =
                    @"
                    DELETE FROM cpu_metrics;
                    DELETE FROM ram_metrics;
                    DELETE FROM disk_metrics;
                    DELETE FROM disk_queue_metrics;
                    DELETE FROM network_metrics;
                    DELETE FROM service_statuses;
                    DELETE FROM incidents;
                ";
            }
            else
            {
                var cutoff = DateTime.UtcNow - maxAge;
                var cutoffStr = cutoff.ToString("O");
                cmd.CommandText =
                    @"
                    DELETE FROM cpu_metrics WHERE timestamp < @cutoff;
                    DELETE FROM ram_metrics WHERE timestamp < @cutoff;
                    DELETE FROM incidents WHERE timestamp < @cutoff;
                ";
                cmd.Parameters.AddWithValue("@cutoff", cutoffStr);
            }

            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        // SQLite connections are opened/closed per operation, nothing to dispose
    }
}
