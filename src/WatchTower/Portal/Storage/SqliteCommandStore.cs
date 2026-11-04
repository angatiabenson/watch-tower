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
                result_json  TEXT,
                result_attempts         INTEGER NOT NULL DEFAULT 0,
                result_next_attempt_utc TEXT,
                result_abandoned_utc    TEXT,
                rejected                INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_commands_status
                ON agent_commands(status, received_at);

            -- Every command id ever seen, kept far longer than agent_commands (which is purged after
            -- CommandPoll:RetentionDays), so a replayed id is never executed twice.
            CREATE TABLE IF NOT EXISTS command_ids (
                command_id     TEXT PRIMARY KEY,
                first_seen_utc TEXT NOT NULL,
                outcome        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_command_ids_seen
                ON command_ids(first_seen_utc);

            -- Databases created before command_ids existed: every stored command is a seen id.
            -- Idempotent, so it also restores the invariant 'every agent_commands row has an id row'.
            INSERT OR IGNORE INTO command_ids (command_id, first_seen_utc, outcome)
                SELECT command_id, received_at, status FROM agent_commands;
            ";
        cmd.ExecuteNonQuery();

        // Databases created before result retries were scheduled per result.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(agent_commands)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }
        foreach (var (column, definition) in new[]
                 {
                     ("result_attempts", "INTEGER NOT NULL DEFAULT 0"),
                     ("result_next_attempt_utc", "TEXT"),
                     ("result_abandoned_utc", "TEXT"),
                     ("rejected", "INTEGER NOT NULL DEFAULT 0"),
                 })
        {
            if (columns.Contains(column))
                continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE agent_commands ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();

            if (column == "rejected")
            {
                // Rejections recorded before the flag existed whose result is still waiting.
                using var flag = conn.CreateCommand();
                flag.CommandText =
                    @"UPDATE agent_commands SET rejected = 1
                      WHERE status = 'Failed' AND result_json IS NOT NULL
                        AND CASE WHEN json_valid(result_json) THEN json_extract(result_json, '$.Message') END
                            LIKE 'Rejected: %'";
                flag.ExecuteNonQuery();
            }
        }

        using var index = conn.CreateCommand();
        index.CommandText =
            @"CREATE INDEX IF NOT EXISTS idx_commands_results_due
                ON agent_commands(result_next_attempt_utc)
                WHERE result_json IS NOT NULL AND result_abandoned_utc IS NULL;
              CREATE INDEX IF NOT EXISTS idx_commands_rejected
                ON agent_commands(rejected, executed_at);";
        index.ExecuteNonQuery();
    }

    /// <summary>Delay before the first retry of an undelivered result; doubled after every failed attempt.</summary>
    public static readonly TimeSpan ResultRetryBaseDelay = TimeSpan.FromMinutes(1);

    /// <summary>Longest delay between two attempts to deliver the same result.</summary>
    public static readonly TimeSpan ResultRetryMaxDelay = TimeSpan.FromMinutes(30);

    /// <summary>1, 2, 4, 8, 16, 30, 30… minutes after the 1st, 2nd, 3rd… failed attempt.</summary>
    public static TimeSpan ResultRetryDelay(int failedAttempts)
    {
        var doublings = Math.Clamp(failedAttempts - 1, 0, 30);
        var delay = ResultRetryBaseDelay * Math.Pow(2, doublings);
        return delay < ResultRetryMaxDelay ? delay : ResultRetryMaxDelay;
    }

    /// <summary>A result is waiting for delivery: stored, not delivered and not given up on.</summary>
    private const string ResultPending = "result_json IS NOT NULL AND result_abandoned_utc IS NULL";

    /// <summary>A fresh result starts its own retry schedule.</summary>
    private const string ResetResultRetry =
        "result_attempts = 0, result_next_attempt_utc = NULL, result_abandoned_utc = NULL";

    /// <summary>
    /// Records <paramref name="commandId"/> as seen. Returns <c>false</c> when it already was.
    /// Must run in the same transaction as the agent_commands insert it guards.
    /// </summary>
    private static bool TryRecordCommandId(SqliteConnection conn, SqliteTransaction tx, string commandId, string outcome, string nowUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            @"INSERT OR IGNORE INTO command_ids (command_id, first_seen_utc, outcome)
              VALUES (@id, @now, @outcome)";
        cmd.Parameters.AddWithValue("@id", commandId);
        cmd.Parameters.AddWithValue("@now", nowUtc);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static void SetOutcome(SqliteConnection conn, SqliteTransaction tx, string commandId, string outcome)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE command_ids SET outcome = @outcome WHERE command_id = @id";
        cmd.Parameters.AddWithValue("@id", commandId);
        cmd.Parameters.AddWithValue("@outcome", outcome);
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

    public Task<bool> SaveCommandAsync(AgentCommandDto command)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var now = DateTime.UtcNow.ToString("O");

            // The id row and the command row are written together, before anything executes: a
            // crash can leave a command recorded but not run, never run but not recorded.
            if (TryRecordCommandId(conn, tx, command.CommandId, "Received", now))
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText =
                    @"INSERT OR IGNORE INTO agent_commands
                        (command_id, action_type, params_json, status, received_at, scheduled_at, issued_at)
                      VALUES (@id, @type, @params, 'Received', @received, @scheduled, @issued)";
                insert.Parameters.AddWithValue("@id", command.CommandId);
                insert.Parameters.AddWithValue("@type", command.ActionType);
                insert.Parameters.AddWithValue("@params", JsonSerializer.Serialize(command.Parameters));
                insert.Parameters.AddWithValue("@received", now);
                // Normalise to UTC "O" so the lexicographic scheduled_at <= now comparison in
                // GetDueScheduledCommandsAsync is always correct regardless of portal formatting.
                insert.Parameters.AddWithValue("@scheduled", NormalizeUtc(command.ScheduledAt) ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("@issued", NormalizeUtc(command.IssuedAt) ?? (object)DBNull.Value);
                insert.ExecuteNonQuery();
            }

            // A known id is never stored again: it is still waiting only if its command row is
            // (deferred, or the agent stopped before claiming it). Once that row is purged only
            // the id row is left, and the command can never run again.
            using var status = conn.CreateCommand();
            status.Transaction = tx;
            status.CommandText = "SELECT status FROM agent_commands WHERE command_id = @id";
            status.Parameters.AddWithValue("@id", command.CommandId);
            var pending = status.ExecuteScalar() is "Received" or "Acknowledged";

            tx.Commit();
            return Task.FromResult(pending);
        }
    }

    public Task<bool> TryRecordRejectedAsync(AgentCommandDto command, string message)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var now = DateTime.UtcNow.ToString("O");
            if (!TryRecordCommandId(conn, tx, command.CommandId, "Rejected", now))
                return Task.FromResult(false);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                @"INSERT OR IGNORE INTO agent_commands
                    (command_id, action_type, params_json, status, received_at, scheduled_at, issued_at,
                     executed_at, result_json, rejected)
                  VALUES (@id, @type, @params, 'Failed', @now, @scheduled, @issued, @now, @result, 1)";
            var result = new CommandResultDto(command.CommandId, false, message, now);
            cmd.Parameters.AddWithValue("@id", command.CommandId);
            cmd.Parameters.AddWithValue("@type", command.ActionType);
            cmd.Parameters.AddWithValue("@params", JsonSerializer.Serialize(command.Parameters));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@scheduled", NormalizeUtc(command.ScheduledAt) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@issued", NormalizeUtc(command.IssuedAt) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@result", JsonSerializer.Serialize(result));
            var inserted = cmd.ExecuteNonQuery() > 0;
            tx.Commit();
            return Task.FromResult(inserted);
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
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                @"UPDATE agent_commands
                  SET status='Executing', acked_at=@now
                  WHERE command_id=@id AND status IN ('Received','Acknowledged')";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", commandId);
            var claimed = cmd.ExecuteNonQuery() > 0;
            if (claimed)
                SetOutcome(conn, tx, commandId, "Executing");
            tx.Commit();
            return Task.FromResult(claimed);
        }
    }

    public Task MarkCompletedAsync(string commandId, CommandResultDto result)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $@"UPDATE agent_commands
                  SET status='Completed', executed_at=@now, result_json=@result, {ResetResultRetry}
                  WHERE command_id=@id";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@result", JsonSerializer.Serialize(result));
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.ExecuteNonQuery();
            SetOutcome(conn, tx, commandId, "Completed");
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(string commandId, string error)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $@"UPDATE agent_commands
                  SET status='Failed', executed_at=@now,
                      result_json=@result, {ResetResultRetry}
                  WHERE command_id=@id";
            var failResult = new CommandResultDto(commandId, false, error, DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@result", JsonSerializer.Serialize(failResult));
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.ExecuteNonQuery();
            SetOutcome(conn, tx, commandId, "Failed");
            tx.Commit();
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
                $@"SELECT command_id, action_type, params_json, status, received_at, scheduled_at, issued_at, result_json
                  FROM agent_commands
                  WHERE status IN ('Completed','Failed') AND {ResultPending}
                  ORDER BY COALESCE(result_next_attempt_utc, executed_at, received_at) ASC, rowid ASC";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
        }
        return Task.FromResult<IReadOnlyList<AgentCommandRecord>>(results);
    }

    public Task<IReadOnlyList<AgentCommandRecord>> GetDueResultsAsync(DateTime nowUtc, int limit)
    {
        var results = new List<AgentCommandRecord>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Ordered by when each result is due, not by age: a result the portal keeps refusing
            // waits out its backoff and cannot hold back the results behind it.
            cmd.CommandText =
                $@"SELECT command_id, action_type, params_json, status, received_at, scheduled_at, issued_at, result_json
                  FROM agent_commands
                  WHERE status IN ('Completed','Failed') AND {ResultPending}
                    AND (result_next_attempt_utc IS NULL OR result_next_attempt_utc <= @now)
                  ORDER BY COALESCE(result_next_attempt_utc, executed_at, received_at) ASC, rowid ASC
                  LIMIT @limit";
            cmd.Parameters.AddWithValue("@now", nowUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("@limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
        }
        return Task.FromResult<IReadOnlyList<AgentCommandRecord>>(results);
    }

    public Task<DateTime?> RecordResultAttemptFailedAsync(string commandId, DateTime nowUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var read = conn.CreateCommand();
            read.Transaction = tx;
            read.CommandText = $"SELECT result_attempts FROM agent_commands WHERE command_id = @id AND {ResultPending}";
            read.Parameters.AddWithValue("@id", commandId);
            if (read.ExecuteScalar() is not long attempts)
                return Task.FromResult<DateTime?>(null);

            var next = nowUtc.ToUniversalTime() + ResultRetryDelay((int)Math.Min(attempts + 1, int.MaxValue));
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                @"UPDATE agent_commands
                  SET result_attempts = result_attempts + 1, result_next_attempt_utc = @next
                  WHERE command_id = @id";
            update.Parameters.AddWithValue("@id", commandId);
            update.Parameters.AddWithValue("@next", next.ToString("O"));
            update.ExecuteNonQuery();
            tx.Commit();
            return Task.FromResult<DateTime?>(next);
        }
    }

    public Task<IReadOnlyList<UndeliveredResult>> AbandonUndeliveredResultsAsync(DateTime producedBeforeUtc, DateTime nowUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var abandoned = ReadUndelivered(conn, tx,
                $@"SELECT command_id, result_attempts, COALESCE(executed_at, received_at) FROM agent_commands
                   WHERE {ResultPending} AND COALESCE(executed_at, received_at) < @cutoff",
                ("@cutoff", producedBeforeUtc.ToUniversalTime().ToString("O")));

            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE agent_commands SET result_abandoned_utc = @now WHERE command_id = @id";
            update.Parameters.AddWithValue("@now", nowUtc.ToUniversalTime().ToString("O"));
            var id = update.Parameters.Add("@id", SqliteType.Text);
            foreach (var result in abandoned)
            {
                id.Value = result.CommandId;
                update.ExecuteNonQuery();
            }

            tx.Commit();
            return Task.FromResult<IReadOnlyList<UndeliveredResult>>(abandoned);
        }
    }

    public Task<TrimmedRejections> TrimRejectionsAsync(int keep)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            // Rejected commands never ran: their row only carries the result, and their id row
            // (command_ids) keeps them from being executed later, so the oldest can go.
            const string Excess =
                @"SELECT command_id FROM agent_commands WHERE rejected = 1
                  ORDER BY COALESCE(executed_at, received_at) DESC, rowid DESC
                  LIMIT -1 OFFSET @keep";
            var keepValue = Math.Max(0, keep);

            var undelivered = ReadUndelivered(conn, tx,
                $@"SELECT command_id, result_attempts, COALESCE(executed_at, received_at) FROM agent_commands
                   WHERE {ResultPending} AND command_id IN ({Excess})",
                ("@keep", keepValue));

            using var delete = conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = $"DELETE FROM agent_commands WHERE command_id IN ({Excess})";
            delete.Parameters.AddWithValue("@keep", keepValue);
            var deleted = delete.ExecuteNonQuery();

            tx.Commit();
            return Task.FromResult(new TrimmedRejections(deleted, undelivered));
        }
    }

    private static List<UndeliveredResult> ReadUndelivered(
        SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var results = new List<UndeliveredResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new UndeliveredResult(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        return results;
    }

    /// <summary>Number of stored rejected commands. For tests and diagnostics.</summary>
    internal int CountRejections()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM agent_commands WHERE rejected = 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
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
                    $@"UPDATE agent_commands
                      SET status = 'Failed', executed_at = @now, result_json = @result, {ResetResultRetry}
                      WHERE command_id = @id AND status = 'Executing'";
                update.Parameters.AddWithValue("@now", now);
                update.Parameters.AddWithValue("@result", JsonSerializer.Serialize(result));
                update.Parameters.AddWithValue("@id", id);
                if (update.ExecuteNonQuery() > 0)
                {
                    SetOutcome(conn, tx, id, "Failed");
                    results.Add(result);
                }
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
            // Rows whose result is still being retried are kept; once it was delivered or given
            // up on (result_abandoned_utc), the row goes like any other, rejections included.
            // The command id itself stays remembered in command_ids.
            cmd.CommandText =
                @"DELETE FROM agent_commands
                  WHERE status IN ('Completed', 'Failed')
                    AND (result_json IS NULL OR result_abandoned_utc IS NOT NULL)
                    AND COALESCE(executed_at, received_at) < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", olderThanUtc.ToUniversalTime().ToString("O"));
            return Task.FromResult(cmd.ExecuteNonQuery());
        }
    }

    public Task<int> PruneCommandIdsAsync(DateTime? seenBeforeUtc, int maxCount)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var removed = 0;

            // An id whose command row still exists is never pruned: that row is the command's
            // state, and the id row is what keeps it from being stored again.
            const string NoCommandRow =
                "NOT EXISTS (SELECT 1 FROM agent_commands a WHERE a.command_id = c.command_id)";

            if (seenBeforeUtc is { } cutoff)
            {
                using var byAge = conn.CreateCommand();
                byAge.Transaction = tx;
                byAge.CommandText =
                    $@"DELETE FROM command_ids
                       WHERE command_id IN (SELECT c.command_id FROM command_ids c
                                            WHERE c.first_seen_utc < @cutoff AND {NoCommandRow})";
                byAge.Parameters.AddWithValue("@cutoff", cutoff.ToUniversalTime().ToString("O"));
                removed += byAge.ExecuteNonQuery();
            }

            if (maxCount > 0)
            {
                using var byCount = conn.CreateCommand();
                byCount.Transaction = tx;
                byCount.CommandText =
                    $@"DELETE FROM command_ids
                       WHERE command_id IN (SELECT c.command_id FROM command_ids c
                                            WHERE {NoCommandRow}
                                            ORDER BY c.first_seen_utc ASC, c.rowid ASC
                                            LIMIT MAX(0, (SELECT COUNT(*) FROM command_ids) - @max))";
                byCount.Parameters.AddWithValue("@max", maxCount);
                removed += byCount.ExecuteNonQuery();
            }

            tx.Commit();
            return Task.FromResult(removed);
        }
    }

    /// <summary>Number of remembered command ids. For tests and diagnostics.</summary>
    internal int CountCommandIds()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM command_ids";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Recorded outcome of <paramref name="commandId"/>, or <c>null</c> if it is not remembered.</summary>
    internal string? GetCommandIdOutcome(string commandId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT outcome FROM command_ids WHERE command_id = @id";
            cmd.Parameters.AddWithValue("@id", commandId);
            return cmd.ExecuteScalar() as string;
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