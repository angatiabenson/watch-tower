using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using WatchTower.Models;

namespace WatchTower.Services.Alerting;

/// <summary>SQLite <see cref="IAlertStateStore"/> sharing the agent database file.</summary>
public sealed class SqliteAlertStateStore : IAlertStateStore
{
    /// <summary>Stored notification and channel-list JSON (property names as declared).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _connectionString;
    private readonly Lock _lock = new();

    public SqliteAlertStateStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS alert_state (
                key              TEXT    PRIMARY KEY COLLATE NOCASE,
                severity         INTEGER NOT NULL,
                incident_type    INTEGER NOT NULL,
                title            TEXT    NOT NULL,
                opened_at        TEXT    NOT NULL,
                last_notified_at TEXT,
                notify_count     INTEGER NOT NULL DEFAULT 0,
                resolved_at      TEXT,
                last_description TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS alert_delivery (
                id                 TEXT    PRIMARY KEY,
                alert_key          TEXT    NOT NULL COLLATE NOCASE,
                kind               INTEGER NOT NULL,
                notification       TEXT    NOT NULL,
                created_at         TEXT    NOT NULL,
                delivered_channels TEXT    NOT NULL DEFAULT '[]',
                failed_attempts    INTEGER NOT NULL DEFAULT 0,
                first_attempt_at   TEXT,
                next_attempt_at    TEXT    NOT NULL,
                last_error         TEXT,
                held               INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS agent_state (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Same per-connection settings as the other stores sharing this file.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public IReadOnlyList<AlertState> LoadAll()
    {
        var results = new List<AlertState>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT key, severity, incident_type, title, opened_at, last_notified_at, notify_count,
                       resolved_at, last_description
                FROM alert_state
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new AlertState(
                    reader.GetString(0),
                    (AlertSeverity)reader.GetInt32(1),
                    (IncidentType)reader.GetInt32(2),
                    reader.GetString(3),
                    ParseUtc(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                    reader.GetString(8)));
            }
        }
        return results;
    }

    public void Upsert(AlertState state)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO alert_state (key, severity, incident_type, title, opened_at, last_notified_at,
                                         notify_count, resolved_at, last_description)
                VALUES (@key, @severity, @type, @title, @opened, @notified, @count, @resolved, @description)
                ON CONFLICT(key) DO UPDATE SET
                    severity = excluded.severity,
                    incident_type = excluded.incident_type,
                    title = excluded.title,
                    opened_at = excluded.opened_at,
                    last_notified_at = excluded.last_notified_at,
                    notify_count = excluded.notify_count,
                    resolved_at = excluded.resolved_at,
                    last_description = excluded.last_description
                """;
            cmd.Parameters.AddWithValue("@key", state.Key);
            cmd.Parameters.AddWithValue("@severity", (int)state.Severity);
            cmd.Parameters.AddWithValue("@type", (int)state.IncidentType);
            cmd.Parameters.AddWithValue("@title", state.Title);
            cmd.Parameters.AddWithValue("@opened", Format(state.OpenedAt));
            cmd.Parameters.AddWithValue("@notified", state.LastNotifiedAt is { } n ? Format(n) : DBNull.Value);
            cmd.Parameters.AddWithValue("@count", state.NotifyCount);
            cmd.Parameters.AddWithValue("@resolved", state.ResolvedAt is { } r ? Format(r) : DBNull.Value);
            cmd.Parameters.AddWithValue("@description", state.LastDescription);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PendingDelivery> LoadDeliveries()
    {
        var results = new List<PendingDelivery>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, notification, created_at, delivered_channels, failed_attempts, first_attempt_at,
                       next_attempt_at, last_error, held
                FROM alert_delivery
                ORDER BY created_at, id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                AlertNotification? notification;
                List<string>? delivered;
                try
                {
                    notification = JsonSerializer.Deserialize<AlertNotification>(reader.GetString(1), JsonOptions);
                    delivered = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOptions);
                }
                catch (JsonException)
                {
                    continue; // unreadable row (written by a different version): skip it
                }
                if (notification is null)
                    continue;
                results.Add(new PendingDelivery(
                    reader.GetString(0),
                    notification,
                    ParseUtc(reader.GetString(2)),
                    delivered ?? [],
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                    ParseUtc(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8) != 0));
            }
        }
        return results;
    }

    public void UpsertDelivery(PendingDelivery delivery)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO alert_delivery (id, alert_key, kind, notification, created_at, delivered_channels,
                                            failed_attempts, first_attempt_at, next_attempt_at, last_error, held)
                VALUES (@id, @key, @kind, @notification, @created, @delivered, @failed, @first, @next, @error, @held)
                ON CONFLICT(id) DO UPDATE SET
                    notification = excluded.notification,
                    delivered_channels = excluded.delivered_channels,
                    failed_attempts = excluded.failed_attempts,
                    first_attempt_at = excluded.first_attempt_at,
                    next_attempt_at = excluded.next_attempt_at,
                    last_error = excluded.last_error,
                    held = excluded.held
                """;
            cmd.Parameters.AddWithValue("@id", delivery.Id);
            cmd.Parameters.AddWithValue("@key", delivery.AlertKey);
            cmd.Parameters.AddWithValue("@kind", (int)delivery.Notification.Kind);
            cmd.Parameters.AddWithValue("@notification", JsonSerializer.Serialize(delivery.Notification, JsonOptions));
            cmd.Parameters.AddWithValue("@created", Format(delivery.CreatedAt));
            cmd.Parameters.AddWithValue("@delivered", JsonSerializer.Serialize(delivery.DeliveredChannels, JsonOptions));
            cmd.Parameters.AddWithValue("@failed", delivery.FailedAttempts);
            cmd.Parameters.AddWithValue("@first", delivery.FirstAttemptAt is { } f ? Format(f) : DBNull.Value);
            cmd.Parameters.AddWithValue("@next", Format(delivery.NextAttemptAt));
            cmd.Parameters.AddWithValue("@error", (object?)delivery.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@held", delivery.Held ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteDelivery(string id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM alert_delivery WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetValue(string key)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM agent_state WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetValue(string key, string value)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO agent_state (key, value, updated_at) VALUES (@key, @value, @now)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@now", Format(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }
    }

    private static string Format(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc))
        .ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}