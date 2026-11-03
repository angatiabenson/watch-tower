using System.Text.Json;

using Microsoft.Data.Sqlite;

using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Storage;

public class SqliteRemoteConfigStore : IRemoteConfigStore, IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public SqliteRemoteConfigStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS remote_config (
                id           INTEGER PRIMARY KEY CHECK (id = 1),
                version      TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                applied_at   TEXT NOT NULL,
                acked_at     TEXT
            );
            ";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var wal = conn.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();
        return conn;
    }

    public Task SaveConfigAsync(RemoteAgentConfigDto config, string version)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO remote_config (id, version, payload_json, applied_at)
                  VALUES (1, @version, @payload, @now)
                  ON CONFLICT(id) DO UPDATE
                  SET version = @version, payload_json = @payload, applied_at = @now, acked_at = NULL";
            cmd.Parameters.AddWithValue("@version", version);
            cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(config));
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetCurrentVersionAsync()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM remote_config WHERE id = 1";
            var result = cmd.ExecuteScalar();
            return Task.FromResult(result as string);
        }
    }

    public Task MarkAcknowledgedAsync(string version)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE remote_config SET acked_at = @now WHERE id = 1 AND version = @version";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@version", version);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<(RemoteAgentConfigDto? Config, string? Version)> GetUnacknowledgedAsync()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT payload_json, version FROM remote_config WHERE id = 1 AND acked_at IS NULL";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<(RemoteAgentConfigDto?, string?)>((null, null));

            var config = JsonSerializer.Deserialize<RemoteAgentConfigDto>(reader.GetString(0));
            var version = reader.GetString(1);
            return Task.FromResult<(RemoteAgentConfigDto?, string?)>((config, version));
        }
    }

    public void Dispose() { }
}