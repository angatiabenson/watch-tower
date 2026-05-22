using System.Text.Json;
using Microsoft.Data.Sqlite;
using WatchTower.Portal.Contracts;

namespace WatchTower.Portal.Storage;

public class SqliteCommandStore : ICommandStore, IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public SqliteCommandStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS agent_commands (
                command_id   TEXT PRIMARY KEY,
                action_type  INTEGER NOT NULL,
                params_json  TEXT    NOT NULL,
                status       TEXT    NOT NULL DEFAULT 'Received',
                received_at  TEXT    NOT NULL,
                scheduled_at TEXT,
                issued_at    TEXT,
                acked_at     TEXT,
                executed_at  TEXT,
                result_json  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_commands_status
                ON agent_commands(status, received_at);
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

    public Task SaveCommandAsync(AgentCommandDto command)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR IGNORE INTO agent_commands
                    (command_id, action_type, params_json, status, received_at, scheduled_at, issued_at)
                  VALUES (@id, @type, @params, 'Received', @received, @scheduled, @issued)";
            cmd.Parameters.AddWithValue("@id", command.CommandId);
            cmd.Parameters.AddWithValue("@type", command.ActionType);
            cmd.Parameters.AddWithValue("@params", JsonSerializer.Serialize(command.Parameters));
            cmd.Parameters.AddWithValue("@received", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@scheduled", command.ScheduledAt ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@issued", command.IssuedAt ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentCommandRecord>> GetPendingCommandsAsync()
    {
        var results = new List<AgentCommandRecord>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT command_id, action_type, params_json, status, received_at, scheduled_at, issued_at, result_json
                  FROM agent_commands
                  WHERE status IN ('Received', 'Acknowledged')
                  ORDER BY received_at ASC";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
        }
        return Task.FromResult<IReadOnlyList<AgentCommandRecord>>(results);
    }

    public Task MarkExecutingAsync(string commandId)
        => UpdateStatus(commandId, "Executing", "acked_at");

    public Task MarkCompletedAsync(string commandId, CommandResultDto result)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"UPDATE agent_commands
                  SET status='Completed', executed_at=@now, result_json=@result
                  WHERE command_id=@id";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@result", JsonSerializer.Serialize(result));
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(string commandId, string error)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"UPDATE agent_commands
                  SET status='Failed', executed_at=@now,
                      result_json=@result
                  WHERE command_id=@id";
            var failResult = new CommandResultDto(commandId, false, error, DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@result", JsonSerializer.Serialize(failResult));
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentCommandRecord>> GetUnsentResultsAsync()
    {
        var results = new List<AgentCommandRecord>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT command_id, action_type, params_json, status, received_at, scheduled_at, issued_at, result_json
                  FROM agent_commands
                  WHERE status IN ('Completed','Failed') AND result_json IS NOT NULL
                  ORDER BY executed_at ASC
                  LIMIT 50";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
        }
        return Task.FromResult<IReadOnlyList<AgentCommandRecord>>(results);
    }

    public Task MarkResultSentAsync(string commandId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE agent_commands SET result_json = NULL WHERE command_id = @id";
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentCommandRecord>> GetDueScheduledCommandsAsync()
    {
        var results = new List<AgentCommandRecord>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT command_id, action_type, params_json, status, received_at, scheduled_at, issued_at, result_json
                  FROM agent_commands
                  WHERE status = 'Received'
                    AND scheduled_at IS NOT NULL
                    AND scheduled_at <= @now
                  ORDER BY scheduled_at ASC";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
        }
        return Task.FromResult<IReadOnlyList<AgentCommandRecord>>(results);
    }

    private Task UpdateStatus(string commandId, string status, string? extraNullColumn = null)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE agent_commands SET status=@status WHERE command_id=@id";
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    private static AgentCommandRecord ReadRecord(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetInt32(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7)
        );

    public void Dispose() { }
}
