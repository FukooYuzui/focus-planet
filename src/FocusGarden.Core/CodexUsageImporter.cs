using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

public sealed record CodexUsageTotals(long InputTokens, long CachedInputTokens, long OutputTokens,
    long ReasoningOutputTokens, long TotalTokens, long EventCount);

public sealed record CodexImportResult(int FilesScanned, int FilesUnchanged, long EventsImported,
    long TotalEvents, int DaysWithUsage, int ReadErrors, long SkippedRecords, DateTimeOffset CompletedAt, string SourceRoot);

/// <summary>Best-effort local Codex token metadata, not billing or account quota.
/// Only numeric usage metadata is indexed. Session text is streamed past, never stored or returned.</summary>
public sealed class CodexUsageImporter : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private readonly SemaphoreSlim importGate = new(1, 1);
    private string? activeSource;
    private bool disposed;
    private const int MaximumLineCharacters = 1024 * 1024;
    public string? ActiveSource { get { lock (gate) { CheckDisposed(); return activeSource; } } }

    public CodexUsageImporter(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:") { dbPath = Path.GetFullPath(dbPath); Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!); }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var c = connection.CreateCommand();
        c.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS codex_usage_settings (id INTEGER PRIMARY KEY CHECK(id=1), source_root TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS codex_usage_files (
                source_root TEXT NOT NULL, path TEXT NOT NULL, length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
                PRIMARY KEY(source_root,path));
            CREATE TABLE IF NOT EXISTS codex_token_events (
                event_id TEXT PRIMARY KEY, timestamp_utc TEXT NOT NULL, local_day TEXT NOT NULL,
                input_tokens INTEGER NOT NULL, cached_input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL, total_tokens INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS codex_usage_file_events (
                source_root TEXT NOT NULL, path TEXT NOT NULL, event_id TEXT NOT NULL REFERENCES codex_token_events(event_id),
                PRIMARY KEY(source_root,path,event_id),
                FOREIGN KEY(source_root,path) REFERENCES codex_usage_files(source_root,path) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS codex_event_day ON codex_token_events(local_day);
            CREATE INDEX IF NOT EXISTS codex_file_event_id ON codex_usage_file_events(event_id);
            """;
        c.ExecuteNonQuery();
        c.CommandText = "SELECT source_root FROM codex_usage_settings WHERE id=1";
        activeSource = c.ExecuteScalar() as string;
    }

    public async Task<CodexImportResult> ImportAsync(string codexHome, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(codexHome));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Codex 数据目录不存在，请选择包含 sessions 的 .codex 目录。");
        await importGate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => ImportCore(root, token), token).ConfigureAwait(false); }
        finally { importGate.Release(); }
    }

    private CodexImportResult ImportCore(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<string, (long Length, long Modified)> known;
        lock (gate)
        {
            CheckDisposed(); known = new(StringComparer.OrdinalIgnoreCase);
            using var c = Command("SELECT path,length,modified_ticks FROM codex_usage_files WHERE source_root=$root", ("$root", root));
            using var r = c.ExecuteReader();
            while (r.Read()) known[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        }
        var errors = 0;
        var catalogErrors = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { "sessions", "archived_sessions" })
            Discover(Path.Combine(root, directory), paths, ref catalogErrors, token);
        errors += catalogErrors;
        var scanned = 0; var unchanged = 0; long imported = 0, skipped = 0;
        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var f = new FileInfo(path);
                var stamp = (Length: f.Length, Modified: f.LastWriteTimeUtc.Ticks);
                if (known.TryGetValue(path, out var previous) && previous == stamp) { unchanged++; continue; }
                var parsed = ReadEvents(path, token);
                skipped += parsed.Skipped;
                token.ThrowIfCancellationRequested();
                imported += CommitFile(root, path, stamp, parsed.Events, token);
                scanned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
        }
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckDisposed();
            using var tx = connection.BeginTransaction();
            // An unreadable directory may contain old files: do not infer deletion from incomplete enumeration.
            if (catalogErrors == 0)
                foreach (var missing in known.Keys.Where(p => !paths.Contains(p)))
                {
                    using var remove = Command("DELETE FROM codex_usage_files WHERE source_root=$root AND path=$path", ("$root", root), ("$path", missing));
                    remove.Transaction = tx; remove.ExecuteNonQuery();
                }
            using (var cleanup = Command("DELETE FROM codex_token_events WHERE NOT EXISTS (SELECT 1 FROM codex_usage_file_events f WHERE f.event_id=codex_token_events.event_id)"))
            { cleanup.Transaction = tx; cleanup.ExecuteNonQuery(); }
            using (var choose = Command("INSERT INTO codex_usage_settings(id,source_root) VALUES(1,$root) ON CONFLICT(id) DO UPDATE SET source_root=excluded.source_root", ("$root", root)))
            { choose.Transaction = tx; choose.ExecuteNonQuery(); }
            tx.Commit(); activeSource = root;
            var totals = GetTotals();
            using var days = Command("SELECT COUNT(DISTINCT local_day) FROM codex_token_events e WHERE EXISTS (SELECT 1 FROM codex_usage_file_events f WHERE f.event_id=e.event_id AND f.source_root=$root)", ("$root", root));
            return new(scanned, unchanged, imported, totals.EventCount, Convert.ToInt32(days.ExecuteScalar()), errors, skipped, DateTimeOffset.Now, root);
        }
    }

    private static void Discover(string directory, HashSet<string> paths, ref int errors, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFiles(current, "*.jsonl")) paths.Add(Path.GetFullPath(path));
                foreach (var sub in Directory.EnumerateDirectories(current))
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
        }
    }

    private static (List<UsageEvent> Events, long Skipped) ReadEvents(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);
        var events = new List<UsageEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Usage? previous = null;
        long skipped = 0;
        foreach (var line in BoundedLines(reader, token))
        {
            if (line == null) { skipped++; continue; }
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var o = doc.RootElement;
                if (!o.TryGetProperty("type", out var type) || type.GetString() != "event_msg" ||
                    !o.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("type", out var payloadType) || payloadType.GetString() != "token_count") continue;
                if (!o.TryGetProperty("timestamp", out var stamp) || stamp.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ||
                    !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("total_token_usage", out var totalJson) || !Usage.TryRead(totalJson, out var total)) { skipped++; continue; }
                Usage? last = null;
                if (info.TryGetProperty("last_token_usage", out var lastJson) && Usage.TryRead(lastJson, out var parsedLast)) last = parsedLast;
                // Rate-limit/status notifications can repeat exactly the same cumulative usage with a new timestamp.
                if (previous == total) { skipped++; continue; }
                var credit = last;
                if (credit == null && previous != null && total.Input >= previous.Value.Input && total.Output >= previous.Value.Output)
                    credit = new Usage(total.Input - previous.Value.Input, Math.Max(0, total.Cached - previous.Value.Cached),
                        total.Output - previous.Value.Output, Math.Max(0, total.Reasoning - previous.Value.Reasoning), Math.Max(0, total.CacheWrite - previous.Value.CacheWrite));
                previous = total;
                // Missing initial/reset history cannot be assigned accurately to this timestamp.
                if (credit == null) { skipped++; continue; }
                var amount = credit.Value.ClampSubsets();
                var canonical = time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + total.Canonical + "|" + (last?.Canonical ?? "missing");
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
                if (!seen.Add(id)) { skipped++; continue; }
                events.Add(new(id, time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    TimeZoneInfo.ConvertTime(time, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), amount));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { skipped++; }
        }
        return (events, skipped);
    }

    private static IEnumerable<string?> BoundedLines(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[65536]; var line = new StringBuilder(); var oversized = false; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    yield return oversized ? null : line.ToString().TrimEnd('\r');
                    line.Clear(); oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Length >= MaximumLineCharacters) { line.Clear(); oversized = true; }
                    else line.Append(ch);
                }
            }
        }
        if (line.Length > 0 || oversized) yield return oversized ? null : line.ToString().TrimEnd('\r');
    }

    private long CommitFile(string root, string path, (long Length, long Modified) stamp, List<UsageEvent> events, CancellationToken token)
    {
        lock (gate)
        {
            CheckDisposed(); token.ThrowIfCancellationRequested();
            using var tx = connection.BeginTransaction();
            using (var file = Command("INSERT INTO codex_usage_files(source_root,path,length,modified_ticks) VALUES($root,$path,$length,$modified) ON CONFLICT(source_root,path) DO UPDATE SET length=excluded.length,modified_ticks=excluded.modified_ticks", ("$root", root), ("$path", path), ("$length", stamp.Length), ("$modified", stamp.Modified)))
            { file.Transaction = tx; file.ExecuteNonQuery(); }
            using (var clear = Command("DELETE FROM codex_usage_file_events WHERE source_root=$root AND path=$path", ("$root", root), ("$path", path)))
            { clear.Transaction = tx; clear.ExecuteNonQuery(); }
            long inserted = 0;
            foreach (var e in events)
            {
                token.ThrowIfCancellationRequested();
                using var insert = Command("INSERT OR IGNORE INTO codex_token_events(event_id,timestamp_utc,local_day,input_tokens,cached_input_tokens,output_tokens,reasoning_output_tokens,total_tokens) VALUES($id,$time,$day,$input,$cached,$output,$reasoning,$total)",
                    ("$id", e.Id), ("$time", e.Time), ("$day", e.Day), ("$input", e.Amount.Input), ("$cached", e.Amount.Cached), ("$output", e.Amount.Output), ("$reasoning", e.Amount.Reasoning), ("$total", e.Amount.Input + e.Amount.Output));
                insert.Transaction = tx; inserted += insert.ExecuteNonQuery();
                using var link = Command("INSERT INTO codex_usage_file_events(source_root,path,event_id) VALUES($root,$path,$id)", ("$root", root), ("$path", path), ("$id", e.Id));
                link.Transaction = tx; link.ExecuteNonQuery();
            }
            tx.Commit(); return inserted;
        }
    }

    public Dictionary<string, long> GetDailyTotals(int year)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        lock (gate)
        {
            CheckDisposed(); var result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (activeSource == null) return result;
            using var c = Command("SELECT local_day,SUM(total_tokens) FROM codex_token_events e WHERE local_day >= $start AND local_day <= $end AND EXISTS (SELECT 1 FROM codex_usage_file_events f WHERE f.event_id=e.event_id AND f.source_root=$root) GROUP BY local_day ORDER BY local_day", ("$start", $"{year:D4}-01-01"), ("$end", $"{year:D4}-12-31"), ("$root", activeSource));
            using var r = c.ExecuteReader(); while (r.Read()) result[r.GetString(0)] = r.GetInt64(1); return result;
        }
    }

    public CodexUsageTotals GetTotals(DateTime? day = null)
    {
        lock (gate)
        {
            CheckDisposed(); if (activeSource == null) return new(0, 0, 0, 0, 0, 0);
            using var c = Command("SELECT COALESCE(SUM(input_tokens),0),COALESCE(SUM(cached_input_tokens),0),COALESCE(SUM(output_tokens),0),COALESCE(SUM(reasoning_output_tokens),0),COALESCE(SUM(total_tokens),0),COUNT(*) FROM codex_token_events e WHERE ($day IS NULL OR local_day=$day) AND EXISTS (SELECT 1 FROM codex_usage_file_events f WHERE f.event_id=e.event_id AND f.source_root=$root)", ("$day", day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$root", activeSource));
            using var r = c.ExecuteReader(); r.Read(); return new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5));
        }
    }

    /// <summary>Deduplicated events in the active source during an inclusive UTC period.
    /// Used for planet rewards so historical usage never credits a newly created game.</summary>
    public Dictionary<string, long> GetDailyTotalsSince(DateTimeOffset cutoff, DateTimeOffset? until = null)
    {
        var end = until ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            CheckDisposed();
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (activeSource == null || end < cutoff) return result;
            using var c = Command("SELECT local_day,SUM(total_tokens) FROM codex_token_events e WHERE timestamp_utc >= $start AND timestamp_utc <= $end AND EXISTS (SELECT 1 FROM codex_usage_file_events f WHERE f.event_id=e.event_id AND f.source_root=$root) GROUP BY local_day ORDER BY local_day",
                ("$start", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                ("$end", end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), ("$root", activeSource));
            using var r = c.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetInt64(1);
            return result;
        }
    }

    private SqliteCommand Command(string text, params (string Name, object? Value)[] parameters)
    {
        var c = connection.CreateCommand(); c.CommandText = text;
        foreach (var (name, value) in parameters) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return c;
    }
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; connection.Dispose(); } }

    private sealed record UsageEvent(string Id, string Time, string Day, Usage Amount);
    private readonly record struct Usage(long Input, long Cached, long Output, long Reasoning, long CacheWrite)
    {
        public string Canonical => FormattableString.Invariant($"{Input},{Cached},{Output},{Reasoning},{CacheWrite}");
        public Usage ClampSubsets() => this with { Cached = Math.Min(Cached, Input), Reasoning = Math.Min(Reasoning, Output) };
        public static bool TryRead(JsonElement value, out Usage usage)
        {
            usage = default;
            if (value.ValueKind != JsonValueKind.Object || !Read(value, "input_tokens", true, out var input) || !Read(value, "output_tokens", true, out var output) ||
                !Read(value, "cached_input_tokens", false, out var cached) || !Read(value, "reasoning_output_tokens", false, out var reasoning) || !Read(value, "cache_write_input_tokens", false, out var write)) return false;
            usage = new(input, cached, output, reasoning, write); return true;
        }
        private static bool Read(JsonElement value, string key, bool required, out long number)
        {
            number = 0;
            if (!value.TryGetProperty(key, out var n)) return !required;
            return n.TryGetInt64(out number) && number >= 0 && number <= 1_000_000_000_000_000L;
        }
    }
}
