using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

/// <summary>Local SQLite persistence. Call AddProgress only for elapsed, awake focus seconds.
/// Time while the process is stopped or suspended is never inferred from wall-clock timestamps.</summary>
public sealed class FocusStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public FocusStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:")
        {
            dbPath = Path.GetFullPath(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
        _connection.Open();
        int schemaVersion=FocusDataProtection.CheckSchema(_connection);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """;
        command.ExecuteNonQuery();
        using var schemaTransaction = _connection.BeginTransaction();
        command.Transaction = schemaTransaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                planned_seconds INTEGER NOT NULL CHECK(planned_seconds>0),
                elapsed_seconds INTEGER NOT NULL DEFAULT 0 CHECK(elapsed_seconds>=0 AND elapsed_seconds<=planned_seconds),
                status TEXT NOT NULL CHECK(status IN ('running','completed','emergency','interrupted')),
                last_checkpoint TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS one_running_session ON sessions(status) WHERE status='running';
            CREATE TABLE IF NOT EXISTS session_days (
                session_id INTEGER NOT NULL REFERENCES sessions(id),
                day TEXT NOT NULL,
                seconds INTEGER NOT NULL CHECK(seconds>0),
                PRIMARY KEY(session_id,day)
            );
            """;
        command.ExecuteNonQuery();
        if(schemaVersion==0){command.CommandText="PRAGMA user_version=1";command.ExecuteNonQuery();}
        schemaTransaction.Commit();
        }
        catch { _connection.Dispose(); throw; }
    }

    public FocusSettings LoadSettings()
    {
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT json FROM settings WHERE id=1";
            var json = command.ExecuteScalar() as string;
            if (json is null) return new FocusSettings();
            try { return ValidateSettings(JsonSerializer.Deserialize<FocusSettings>(json) ?? new FocusSettings()); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Hand-edited/old invalid settings must not make the recovery UI unusable.
                return new FocusSettings();
            }
        }
    }

    public void SaveSettings(FocusSettings settings)
    {
        var normalized = ValidateSettings(settings);
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO settings(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(normalized));
            command.ExecuteNonQuery();
        }
    }

    public long BeginSession(string task, int plannedSeconds, DateTimeOffset now)
    {
        task = (task ?? "").Trim();
        if (task.Length > 200) throw new ArgumentException("专注任务不能超过 200 个字符。", nameof(task));
        if (plannedSeconds is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(plannedSeconds));
        if (task.Length == 0) task = "专注时光";
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sessions(task,started_at,planned_seconds,status,last_checkpoint)
                VALUES($task,$now,$planned,'running',$now);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$task", task);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$planned", plannedSeconds);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>Atomically persist an integer duration and its local-day allocation. Credit is capped at the planned duration.
    /// Calls represent distinct elapsed intervals; callers must not replay a successful delta.</summary>
    public void AddProgress(long id, int seconds, DateTimeOffset intervalEnd)
    {
        if (seconds is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds == 0) return;
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction();
            using var query = _connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT planned_seconds-elapsed_seconds,status FROM sessions WHERE id=$id";
            query.Parameters.AddWithValue("$id", id);
            int remaining;
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read()) throw new ArgumentException("专注记录不存在。", nameof(id));
                if (reader.GetString(1) != "running") throw new InvalidOperationException("已经结束的专注不能继续计时。");
                remaining = reader.GetInt32(0);
            }
            var accepted = Math.Min(seconds, remaining);
            if (accepted == 0) { transaction.Commit(); return; }
            var intervalStart = intervalEnd.AddSeconds(-seconds);
            foreach (var (day, amount) in AllocateLocalDays(intervalStart, accepted))
            {
                using var daily = _connection.CreateCommand();
                daily.Transaction = transaction;
                daily.CommandText = """
                    INSERT INTO session_days(session_id,day,seconds) VALUES($id,$day,$seconds)
                    ON CONFLICT(session_id,day) DO UPDATE SET seconds=seconds+excluded.seconds
                    """;
                daily.Parameters.AddWithValue("$id", id);
                daily.Parameters.AddWithValue("$day", day);
                daily.Parameters.AddWithValue("$seconds", amount);
                daily.ExecuteNonQuery();
            }
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE sessions SET elapsed_seconds=elapsed_seconds+$seconds,last_checkpoint=$end WHERE id=$id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$seconds", accepted);
            update.Parameters.AddWithValue("$end", Format(intervalStart.AddSeconds(accepted)));
            update.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void EndSession(long id, string status, DateTimeOffset now)
    {
        if (status is not ("completed" or "emergency" or "interrupted"))
            throw new ArgumentException("结束状态无效。", nameof(status));
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions SET status=$status,ended_at=$now WHERE id=$id AND status='running'
                AND ($status<>'completed' OR elapsed_seconds=planned_seconds)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$now", Format(now));
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("记录不存在、已经结束，或尚未达到完成时长。");
        }
    }

    /// <summary>Mark a previous process's running session interrupted at its last persisted checkpoint.
    /// The provided recovery time is deliberately not used to invent additional focus seconds.</summary>
    public void RecoverInterrupted(DateTimeOffset now)
    {
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET status='interrupted',ended_at=last_checkpoint WHERE status='running'";
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FocusSession> GetSessions(int limit = 200)
    {
        if (limit is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id,task,started_at,ended_at,planned_seconds,elapsed_seconds,status FROM sessions ORDER BY id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var result = new List<FocusSession>();
            while (reader.Read())
                result.Add(new FocusSession(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6)));
            return result;
        }
    }

    /// <summary>Actual focus seconds, including completed, emergency and interrupted sessions. Keys are local yyyy-MM-dd.</summary>
    public Dictionary<string, int> GetDailyTotals(int year)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT day,SUM(seconds) FROM session_days WHERE day >= $start AND day <= $end GROUP BY day ORDER BY day";
            command.Parameters.AddWithValue("$start", $"{year:D4}-01-01");
            command.Parameters.AddWithValue("$end", $"{year:D4}-12-31");
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt32(1));
            return result;
        }
    }

    public Dictionary<string, int> GetAllDailyTotals()
    {
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT day,SUM(seconds) FROM session_days GROUP BY day ORDER BY day";
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt32(1));
            return result;
        }
    }

    public Dictionary<string, int> GetDailyTotalsSince(DateTime date)
    {
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT day,SUM(seconds) FROM session_days WHERE day >= $start GROUP BY day ORDER BY day";
            command.Parameters.AddWithValue("$start", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt32(1));
            return result;
        }
    }

    /// <summary>Aggregate in SQLite; materialize only the twelve rows actually displayed.</summary>
    public FocusStatisticsSnapshot GetStatistics(int year, DateTime today)
    {
        if (year is < 1 or >= 9999) throw new ArgumentOutOfRangeException(nameof(year));
        lock (_gate)
        {
            CheckDisposed();
            var daily = GetDailyTotals(year);
            var current = year == today.Year ? daily : GetDailyTotals(today.Year);
            current.TryGetValue(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var todaySeconds);
            using var count = _connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sessions WHERE status='completed' AND julianday(ended_at)>=julianday($start) AND julianday(ended_at)<julianday($end)";
            var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local);
            count.Parameters.AddWithValue("$start", Format(new DateTimeOffset(start)));
            count.Parameters.AddWithValue("$end", Format(new DateTimeOffset(start.AddYears(1))));
            var completed = Convert.ToInt32(count.ExecuteScalar());
            return new(year, today.Date, daily, todaySeconds, completed, GetSessions(12));
        }
    }

    private static IEnumerable<KeyValuePair<string, int>> AllocateLocalDays(DateTimeOffset start, int seconds)
    {
        var end = start.AddSeconds(seconds);
        var cursor = start;
        var allocated = 0;
        while (cursor < end)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, TimeZoneInfo.Local);
            var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            var next = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
            var segmentEnd = next < end ? next : end;
            // Whole-second accounting; the last local day receives any subsecond rounding remainder.
            var amount = segmentEnd == end ? seconds - allocated : (int)Math.Floor((segmentEnd - cursor).TotalSeconds);
            if (amount > 0) yield return new KeyValuePair<string, int>(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), amount);
            allocated += amount;
            cursor = segmentEnd;
        }
    }

    private static FocusSettings ValidateSettings(FocusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FocusMinutes is < 1 or > 240) throw new ArgumentException("专注时长应为 1–240 分钟。");
        if (settings.BreakMinutes is < 1 or > 60) throw new ArgumentException("休息时长应为 1–60 分钟。");
        if (settings.BlockedApps is null || settings.BlockedDomains is null) throw new ArgumentException("限制列表不能为空值。");
        if (settings.BlockedApps.Length > 100 || settings.BlockedDomains.Length > 200) throw new ArgumentException("限制列表过长。");
        var allowedApps = FocusApplicationPolicy.NormalizeAllowedApps(settings.AllowedApps, settings.WhitelistEnabled);
        return new FocusSettings
        {
            FocusMinutes = settings.FocusMinutes,
            BreakMinutes = settings.BreakMinutes,
            Immersive = settings.Immersive,
            StrictFocus = settings.StrictFocus,
            WhitelistEnabled = settings.WhitelistEnabled,
            AllowedApps = allowedApps,
            HideTaskbar = settings.HideTaskbar,
            BlockDesktopShortcuts = settings.BlockDesktopShortcuts,
            BlockedApps = settings.BlockedApps.Where(x => !string.IsNullOrWhiteSpace(x)).Select(AppRules.Normalize).Distinct().ToArray(),
            BlockedDomains = settings.BlockedDomains.Where(x => !string.IsNullOrWhiteSpace(x)).Select(DomainRules.Normalize).Distinct().ToArray()
        };
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
