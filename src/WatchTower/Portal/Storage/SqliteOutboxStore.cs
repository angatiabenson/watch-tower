using Microsoft.Data.Sqlite;

namespace WatchTower.Portal.Storage;

public class SqliteOutboxStore : IOutboxStore, IPortalSyncState, IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public SqliteOutboxStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS outbox_messages (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                message_type   TEXT    NOT NULL,
                payload_json   TEXT    NOT NULL,
                priority       INTEGER NOT NULL DEFAULT 0,
                status         TEXT    NOT NULL DEFAULT 'Pending',
                attempts       INTEGER NOT NULL DEFAULT 0,
                last_error     TEXT,
                created_at     TEXT    NOT NULL,
                sent_at        TEXT,
                next_retry_at  TEXT,
                cpu_rowid      INTEGER,
                ram_rowid      INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_outbox_status_priority
                ON outbox_messages(status, priority DESC, next_retry_at);

            CREATE TABLE IF NOT EXISTS portal_sync_state (
                metric_type          TEXT PRIMARY KEY,
                last_forwarded_rowid INTEGER NOT NULL DEFAULT 0,
                updated_at           TEXT    NOT NULL
            );
            ";
        cmd.ExecuteNonQuery();

        // Migrate older databases that predate the rowid-coverage columns.
        EnsureColumn(conn, "outbox_messages", "cpu_rowid", "INTEGER");
        EnsureColumn(conn, "outbox_messages", "ram_rowid", "INTEGER");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @col";
            check.Parameters.AddWithValue("@col", column);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
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

    // ── IOutboxStore ─────────────────────────────────────────────────────────

    public Task AddMessageAsync(string messageType, string payloadJson, int priority = 0, long? cpuRowId = null, long? ramRowId = null)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO outbox_messages (message_type, payload_json, priority, created_at, cpu_rowid, ram_rowid)
                  VALUES (@type, @payload, @priority, @created, @cpuRow, @ramRow)";
            cmd.Parameters.AddWithValue("@type", messageType);
            cmd.Parameters.AddWithValue("@payload", payloadJson);
            cmd.Parameters.AddWithValue("@priority", priority);
            cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@cpuRow", cpuRowId.HasValue ? cpuRowId.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ramRow", ramRowId.HasValue ? ramRowId.Value : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int limit)
    {
        var results = new List<OutboxMessage>();
        var now = DateTime.UtcNow.ToString("O");

        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT id, message_type, payload_json, priority, attempts, cpu_rowid, ram_rowid
                  FROM outbox_messages
                  WHERE status = 'Pending'
                    AND (next_retry_at IS NULL OR next_retry_at <= @now)
                  ORDER BY priority DESC, id ASC
                  LIMIT @limit";
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new OutboxMessage(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6)
                ));
            }
        }
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(results);
    }

    public Task MarkSentAsync(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE outbox_messages SET status='Sent', sent_at=@now WHERE id=@id";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(long id, string error, DateTime? nextRetryAt, int maxAttempts)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"UPDATE outbox_messages
                  SET attempts = attempts + 1,
                      last_error = @error,
                      next_retry_at = @retry,
                      status = CASE WHEN attempts + 1 >= @maxAttempts THEN 'Failed' ELSE 'Pending' END
                  WHERE id = @id";
            cmd.Parameters.AddWithValue("@error", error);
            cmd.Parameters.AddWithValue("@retry", nextRetryAt.HasValue ? nextRetryAt.Value.ToString("O") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@maxAttempts", maxAttempts);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task PurgeDeliveredAsync(TimeSpan maxAge)
    {
        var cutoff = (DateTime.UtcNow - maxAge).ToString("O");
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM outbox_messages WHERE status IN ('Sent','Failed') AND created_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    // ── IPortalSyncState ─────────────────────────────────────────────────────

    public Task<long> GetLastForwardedRowIdAsync(string metricType)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT last_forwarded_rowid FROM portal_sync_state WHERE metric_type = @type";
            cmd.Parameters.AddWithValue("@type", metricType);
            var result = cmd.ExecuteScalar();
            return Task.FromResult(result is long v ? v : 0L);
        }
    }

    public Task SetLastForwardedRowIdAsync(string metricType, long rowId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO portal_sync_state (metric_type, last_forwarded_rowid, updated_at)
                  VALUES (@type, @rowid, @now)
                  ON CONFLICT(metric_type) DO UPDATE
                  SET last_forwarded_rowid = @rowid, updated_at = @now";
            cmd.Parameters.AddWithValue("@type", metricType);
            cmd.Parameters.AddWithValue("@rowid", rowId);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
