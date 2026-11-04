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
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
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
            // Normalise to UTC "O" so the lexicographic scheduled_at <= now comparison in
            // GetDueScheduledCommandsAsync is always correct regardless of portal formatting.
            cmd.Parameters.AddWithValue("@scheduled", NormalizeUtc(command.ScheduledAt) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@issued", NormalizeUtc(command.IssuedAt) ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryRecordRejectedAsync(AgentCommandDto command, string message)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT OR IGNORE INTO agent_commands
                    (command_id, action_type, params_json, status, received_at, scheduled_at, issued_at,
                     executed_at, result_json)
                  VALUES (@id, @type, @params, 'Failed', @now, @scheduled, @issued, @now, @result)";
            var now = DateTime.UtcNow.ToString("O");
            var result = new CommandResultDto(command.CommandId, false, message, now);
            cmd.Parameters.AddWithValue("@id", command.CommandId);
            cmd.Parameters.AddWithValue("@type", command.ActionType);
            cmd.Parameters.AddWithValue("@params", JsonSerializer.Serialize(command.Parameters));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@scheduled", NormalizeUtc(command.ScheduledAt) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@issued", NormalizeUtc(command.IssuedAt) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@result", JsonSerializer.Serialize(result));
            return Task.FromResult(cmd.ExecuteNonQuery() > 0);
        }
    }

    private static string? NormalizeUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt)
            ? dt.ToUniversalTime().ToString("O")
            : value;
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

    public Task<bool> TryBeginExecutionAsync(string commandId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"UPDATE agent_commands
                  SET status='Executing', acked_at=@now
                  WHERE command_id=@id AND status IN ('Received','Acknowledged')";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", commandId);
            var rows = cmd.ExecuteNonQuery();
            return Task.FromResult(rows > 0);
        }
    }

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

    public Task<IReadOnlyList<CommandResultDto>> FailInterruptedCommandsAsync(string message)
    {
        var results = new List<CommandResultDto>();
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            var ids = new List<string>();
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT command_id FROM agent_commands WHERE status = 'Executing'";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetString(0));
            }

            var now = DateTime.UtcNow.ToString("O");
            foreach (var id in ids)
            {
                var result = new CommandResultDto(id, false, message, now);
                using var update = conn.CreateCommand();
                update.Transaction = tx;
                update.CommandText =
                    @"UPDATE agent_commands
                      SET status = 'Failed', executed_at = @now, result_json = @result
                      WHERE command_id = @id AND status = 'Executing'";
                update.Parameters.AddWithValue("@now", now);
                update.Parameters.AddWithValue("@result", JsonSerializer.Serialize(result));
                update.Parameters.AddWithValue("@id", id);
                if (update.ExecuteNonQuery() > 0)
                    results.Add(result);
            }

            tx.Commit();
        }
        return Task.FromResult<IReadOnlyList<CommandResultDto>>(results);
    }

    public Task<int> PurgeFinishedCommandsAsync(DateTime olderThanUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // executed_at/received_at are stored as UTC "O" strings, so lexicographic order is
            // chronological. Rows that never recorded executed_at fall back to received_at.
            // Rows whose result has not been accepted by the portal yet (result_json still set)
            // are kept: results are retried until delivered, however old they are.
            cmd.CommandText =
                @"DELETE FROM agent_commands
                  WHERE status IN ('Completed', 'Failed')
                    AND result_json IS NULL
                    AND COALESCE(executed_at, received_at) < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", olderThanUtc.ToUniversalTime().ToString("O"));
            return Task.FromResult(cmd.ExecuteNonQuery());
        }
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