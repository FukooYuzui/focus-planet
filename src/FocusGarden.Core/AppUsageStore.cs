using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

public sealed record AppUsageRow(string AppName, int Seconds);
public sealed record AppUsageCredit(string AppName, DateTime Day, int Seconds);

/// <summary>Stores daily foreground-active seconds only; it never reconstructs past usage.</summary>
public sealed class AppUsageStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private bool disposed;

    public AppUsageStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:")
        {
            dbPath = Path.GetFullPath(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=1000;
            CREATE TABLE IF NOT EXISTS app_usage_days (
                day TEXT NOT NULL,
                app_name TEXT NOT NULL,
                seconds INTEGER NOT NULL CHECK(seconds > 0),
                PRIMARY KEY(day, app_name)
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Records one known interval; split by local natural date, never elapsed wall-clock gaps.</summary>
    public void AddUsage(string appName, int seconds, DateTimeOffset intervalEnd)
    {
        appName = UsageCalendar.NormalizeName(appName);
        if (seconds is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds == 0) return;
        var segments = UsageCalendar.Split(intervalEnd.AddSeconds(-seconds), seconds).ToArray();
        var assigned = 0;
        var credits = new List<AppUsageCredit>();
        for (int i = 0; i < segments.Length; i++)
        {
            var amount = i == segments.Length - 1 ? seconds - assigned : (int)Math.Floor(segments[i].Seconds);
            if (amount > 0) credits.Add(new AppUsageCredit(appName, segments[i].Day, amount));
            assigned += amount;
        }
        AddCredits(credits);
    }

    /// <summary>Atomically commits already day-allocated credits, preserving subsecond sampling boundaries.</summary>
    public void AddCredits(IReadOnlyList<AppUsageCredit> credits)
    {
        ArgumentNullException.ThrowIfNull(credits);
        var normalized = credits.Select(c => new AppUsageCredit(UsageCalendar.NormalizeName(c.AppName), c.Day.Date,
            c.Seconds is > 0 and <= 86400 ? c.Seconds : throw new ArgumentOutOfRangeException(nameof(credits)))).ToArray();
        if (normalized.Length == 0) return;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var transaction = connection.BeginTransaction();
            foreach (var credit in normalized)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO app_usage_days(day,app_name,seconds) VALUES($day,$app,$seconds)
                    ON CONFLICT(day,app_name) DO UPDATE SET seconds=seconds+excluded.seconds
                    """;
                command.Parameters.AddWithValue("$day", credit.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$app", credit.AppName);
                command.Parameters.AddWithValue("$seconds", credit.Seconds);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<AppUsageRow> GetDailyUsage(DateTime date)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT app_name,seconds FROM app_usage_days WHERE day=$day ORDER BY seconds DESC,app_name ASC";
            command.Parameters.AddWithValue("$day", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            var rows = new List<AppUsageRow>();
            while (reader.Read()) rows.Add(new AppUsageRow(reader.GetString(0), reader.GetInt32(1)));
            return rows;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            connection.Dispose();
        }
    }
}

internal static class UsageCalendar
{
    public static string NormalizeName(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        appName = appName.Trim().ToLowerInvariant();
        if (appName.Length > 260 || appName.Any(char.IsControl) || appName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("只接受应用进程名称。", nameof(appName));
        return appName.EndsWith(".exe", StringComparison.Ordinal) ? appName : appName + ".exe";
    }

    public static IEnumerable<(DateTime Day, double Seconds)> Split(DateTimeOffset start, double seconds)
    {
        var end = start.AddSeconds(seconds);
        var cursor = start;
        var assigned = 0.0;
        while (cursor < end)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, TimeZoneInfo.Local);
            var nextLocal = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            // A few time zones change at midnight; move to the next valid wall-clock instant.
            while (TimeZoneInfo.Local.IsInvalidTime(nextLocal)) nextLocal = nextLocal.AddMinutes(1);
            var next = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocal, TimeZoneInfo.Local));
            if (next <= cursor) throw new InvalidOperationException("无法划分本地日期边界。");
            var boundary = next < end ? next : end;
            // DateTimeOffset rounds to 100 ns. Preserve the supplied monotonic duration in the final segment.
            var amount = boundary == end ? seconds - assigned : (boundary - cursor).TotalSeconds;
            yield return (local.Date, amount);
            assigned += amount;
            cursor = boundary;
        }
    }
}
