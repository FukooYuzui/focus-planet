using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FocusGarden.App;
using FocusGarden.Core;
using Microsoft.Data.Sqlite;

var startedAt = DateTimeOffset.UtcNow;
var results = new List<TestResult>();
var junctions = new List<string>();
var tempParent = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"));
var scratch = Path.Combine(tempParent, "FocusGarden-upgrade-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var outputIndex = Array.IndexOf(args, "--output-dir");
string? outputDirectory = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : null;

try
{
    Run("first installation with no database creates no backup", () =>
    {
        var directory = NewDirectory("fresh-install");
        Equal<string?>(null, FocusDataProtection.PrepareForVersion(directory, "0.6.1"));
        Equal(false, File.Exists(Path.Combine(directory, "focus.db")));
        Equal(0, BackupFiles(directory).Length);
    });

    Run("v0.1.0 backup preserves every legacy row and exact cross-day seconds", () =>
    {
        var directory = FixtureDirectory("legacy-backup");
        var db = Database(directory);
        var before = Snapshot(db);
        var bytesBefore = Hash(db);
        var backup = RequiredBackup(FocusDataProtection.PrepareForVersion(directory, "0.6.1"), directory, "0.6.1");
        Equal(before, Snapshot(backup));
        Equal(before, Snapshot(db));
        Equal(bytesBefore, Hash(db));
        CheckExactLegacyTotals(backup);
        CheckExactLegacyTotals(db);
        Equal("ok", Scalar<string>(backup, "PRAGMA integrity_check"));
        Equal(1L, Scalar<long>(backup, "PRAGMA user_version"));
        True(File.Exists(Marker(directory)), "Successful preparation did not persist a version marker.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Marker(directory)));
        Equal(JsonValueKind.Object, marker.RootElement.ValueKind);
    });

    Run("recovering a legacy running session changes only status and end timestamp", () =>
    {
        var directory = FixtureDirectory("legacy-recovery");
        var db = Database(directory);
        var immutableBefore = ImmutableSnapshot(db);
        var finishedBefore = Query(db, "SELECT * FROM sessions WHERE status<>'running' ORDER BY id");
        FocusDataProtection.PrepareForVersion(directory, "0.6.1");
        using (var store = new FocusStore(db))
        {
            store.RecoverInterrupted(DateTimeOffset.Parse("2035-01-01T00:00:00+08:00", CultureInfo.InvariantCulture));
            var recovered = store.GetSessions().Single(s => s.Id == 42);
            Equal("interrupted", recovered.Status);
            Equal(11, recovered.ElapsedSeconds);
            Equal(DateTimeOffset.Parse("2026-10-27T10:00:11+08:00", CultureInfo.InvariantCulture), recovered.EndedAt);
            store.RecoverInterrupted(DateTimeOffset.Parse("2038-01-01T00:00:00+08:00", CultureInfo.InvariantCulture));
        }
        Equal(immutableBefore, ImmutableSnapshot(db));
        Equal(finishedBefore, Query(db, "SELECT * FROM sessions WHERE id<>42 ORDER BY id"));
        Equal(0L, Scalar<long>(db, "SELECT COUNT(*) FROM sessions WHERE status='running'"));
        Equal(1L, Scalar<long>(db, "SELECT COUNT(*) FROM sessions WHERE id=42 AND ended_at=last_checkpoint"));
        CheckExactLegacyTotals(db);
    });

    Run("multiple version launches and repeated launches never duplicate focus totals", () =>
    {
        var directory = FixtureDirectory("repeated-upgrades");
        var db = Database(directory);
        string? recoveredSnapshot = null;
        int expectedBackups = 0;
        foreach (var version in new[] { "0.2.0", "0.3.0", "0.6.1", "0.7.0", "0.7.1", "0.7.2" })
        {
            RequiredBackup(FocusDataProtection.PrepareForVersion(directory, version), directory, version);
            expectedBackups++;
            for (int launch = 0; launch < 3; launch++)
            {
                Equal<string?>(null, FocusDataProtection.PrepareForVersion(directory, version));
                using (var store = new FocusStore(db)) store.RecoverInterrupted(DateTimeOffset.UtcNow);
                var currentSnapshot = Snapshot(db);
                recoveredSnapshot ??= currentSnapshot;
                Equal(recoveredSnapshot, currentSnapshot);
                CheckExactLegacyTotals(db);
                Equal(expectedBackups, BackupFiles(directory).Length);
            }
        }
        Equal(4L, Scalar<long>(db, "SELECT COUNT(*) FROM sessions"));
        Equal(42L, Scalar<long>(db, "SELECT seq FROM sqlite_sequence WHERE name='sessions'"));
    });

    Run("same version does not rebackup after ordinary new focus records", () =>
    {
        var directory = FixtureDirectory("same-version-new-records");
        var db = Database(directory);
        var backup = RequiredBackup(FocusDataProtection.PrepareForVersion(directory, "0.6.1"), directory, "0.6.1");
        using (var store = new FocusStore(db))
        {
            store.RecoverInterrupted(DateTimeOffset.UtcNow);
            var time = DateTimeOffset.Parse("2026-10-28T10:00:00+08:00", CultureInfo.InvariantCulture);
            var id = store.BeginSession("升级后新增的合成记录", 2, time);
            Equal(43L, id);
            store.AddProgress(id, 2, time.AddSeconds(2));
            store.EndSession(id, "completed", time.AddSeconds(2));
        }
        var current = Snapshot(db);
        Equal<string?>(null, FocusDataProtection.PrepareForVersion(directory, "0.6.1"));
        Equal(current, Snapshot(db));
        Equal(1, BackupFiles(directory).Length);
        Equal(263L, Scalar<long>(backup, "SELECT SUM(elapsed_seconds) FROM sessions"));
        Equal(265L, Scalar<long>(db, "SELECT SUM(elapsed_seconds) FROM sessions"));
        Equal(265L, Scalar<long>(db, "SELECT SUM(seconds) FROM session_days"));
    });

    Run("online backup includes committed WAL records not present in main database file", () =>
    {
        var directory = FixtureDirectory("wal-uncheckpointed");
        var db = Database(directory);
        using var writer = LegacyV010Fixture.Open(db);
        LegacyV010Fixture.Execute(writer, "PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE);");
        LegacyV010Fixture.Execute(writer, """
            BEGIN;
            INSERT INTO sessions(id,task,started_at,ended_at,planned_seconds,elapsed_seconds,status,last_checkpoint)
            VALUES(43,'仅在WAL中的合成记录','2026-10-28T12:00:00+08:00','2026-10-28T12:00:17+08:00',17,17,'completed','2026-10-28T12:00:17+08:00');
            INSERT INTO session_days(session_id,day,seconds) VALUES(43,'2026-10-28',17);
            COMMIT;
            """);
        True(File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 32, "Fixture did not leave committed frames in WAL.");
        var rawCopy = Path.Combine(directory, "main-file-without-wal.db");
        File.Copy(db, rawCopy);
        Equal(4L, Scalar<long>(rawCopy, "SELECT COUNT(*) FROM sessions"));
        Equal(5L, Scalar<long>(db, "SELECT COUNT(*) FROM sessions"));
        var before = Snapshot(db);
        var backup = RequiredBackup(FocusDataProtection.PrepareForVersion(directory, "0.6.1"), directory, "0.6.1");
        Equal(before, Snapshot(backup));
        Equal(before, Snapshot(db));
        Equal(280L, Scalar<long>(backup, "SELECT SUM(elapsed_seconds) FROM sessions"));
        Equal(280L, Scalar<long>(backup, "SELECT SUM(seconds) FROM session_days"));
        Equal("ok", Scalar<string>(backup, "PRAGMA integrity_check"));
        var isolatedDirectory = NewDirectory("isolated-wal-backup");
        var isolatedBackup = Path.Combine(isolatedDirectory, "only-backup.db");
        RequireScratchPath(backup);
        RequireScratchPath(isolatedBackup);
        File.Move(backup, isolatedBackup);
        Equal(1, Directory.GetFileSystemEntries(isolatedDirectory).Length);
        Equal("delete", Scalar<string>(isolatedBackup, "PRAGMA journal_mode"));
        Equal(before, Snapshot(isolatedBackup));
        Equal("ok", Scalar<string>(isolatedBackup, "PRAGMA integrity_check"));
        Equal(1, Directory.GetFileSystemEntries(isolatedDirectory).Length);
        Equal(false, File.Exists(isolatedBackup + "-wal"));
        Equal(false, File.Exists(isolatedBackup + "-shm"));
        Equal(false, File.Exists(isolatedBackup + "-journal"));
    });

    Run("backup destination failure preserves source bytes and does not mark completion", () =>
    {
        var directory = FixtureDirectory("backup-path-failure");
        var db = Database(directory);
        var before = Snapshot(db);
        var bytesBefore = Hash(db);
        File.WriteAllText(Path.Combine(directory, "backups"), "Intentional test fixture: this path is a file.");
        ThrowsAny(() => FocusDataProtection.PrepareForVersion(directory, "0.6.1"));
        Equal(before, Snapshot(db));
        Equal(bytesBefore, Hash(db));
        Equal(false, File.Exists(Marker(directory)));
    });

    Run("failed next-version backup keeps the previous successful version marker", () =>
    {
        var directory = FixtureDirectory("prior-marker-failure");
        var db = Database(directory);
        var previousBackup = RequiredBackup(FocusDataProtection.PrepareForVersion(directory, "0.6.0"), directory, "0.6.0");
        var markerBefore = File.ReadAllText(Marker(directory));
        var before = Snapshot(db);
        var bytesBefore = Hash(db);
        var backupDirectory = Path.Combine(directory, "backups");
        var movedDirectory = Path.Combine(directory, "preserved-earlier-backups");
        Directory.Move(backupDirectory, movedDirectory);
        File.WriteAllText(backupDirectory, "Intentional failure for the next version.");
        ThrowsAny(() => FocusDataProtection.PrepareForVersion(directory, "0.6.1"));
        Equal(markerBefore, File.ReadAllText(Marker(directory)));
        Equal(before, Snapshot(db));
        Equal(bytesBefore, Hash(db));
        Equal(before, Snapshot(Path.Combine(movedDirectory, Path.GetFileName(previousBackup))));
    });

    Run("future schema is rejected without downgrade or source modification", () =>
    {
        var directory = FixtureDirectory("future-schema");
        var db = Database(directory);
        using (var connection = LegacyV010Fixture.Open(db))
            LegacyV010Fixture.Execute(connection, "CREATE TABLE future_private_data(id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO future_private_data VALUES(1,'must survive'); PRAGMA user_version=99;");
        var before = Snapshot(db);
        var bytesBefore = Hash(db);
        var futureBefore = Query(db, "SELECT * FROM future_private_data ORDER BY id");
        ThrowsAny(() => FocusDataProtection.PrepareForVersion(directory, "0.6.1"));
        Equal(before, Snapshot(db));
        Equal(bytesBefore, Hash(db));
        Equal(false, File.Exists(Marker(directory)));
        ThrowsAny(() => { using var store = new FocusStore(db); });
        Equal(99L, Scalar<long>(db, "PRAGMA user_version"));
        Equal(before, Snapshot(db));
        Equal(bytesBefore, Hash(db));
        Equal(futureBefore, Query(db, "SELECT * FROM future_private_data ORDER BY id"));
    });

    Run("corrupt database is rejected and never replaced by an empty database", () =>
    {
        var directory = NewDirectory("corrupt-database");
        var db = Database(directory);
        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("This is deliberately NOT a SQLite database. 合成损坏库，不可重置。\n");
        File.WriteAllBytes(db, corruptBytes);
        var before = Hash(db);
        ThrowsAny(() => FocusDataProtection.PrepareForVersion(directory, "0.6.1"));
        Equal(before, Hash(db));
        Equal((long)corruptBytes.Length, new FileInfo(db).Length);
        Equal(false, File.Exists(Marker(directory)));
        ThrowsAny(() => { using var store = new FocusStore(db); });
        Equal(before, Hash(db));
        Equal((long)corruptBytes.Length, new FileInfo(db).Length);
        Equal(0, BackupFiles(directory).Length);
    });

    Run("real Windows junction shares directory identity including nonexistent trailing folders", () =>
    {
        var directory = NewDirectory("junction-identity");
        var target = Path.Combine(directory, "physical-data");
        var alias = Path.Combine(directory, "alias-data");
        Directory.CreateDirectory(target);
        RequireScratchPath(target);
        RequireScratchPath(alias);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:FOCUS_UPGRADE_TEST_ALIAS -Target $env:FOCUS_UPGRADE_TEST_TARGET | Out-Null");
        start.Environment["FOCUS_UPGRADE_TEST_ALIAS"] = alias;
        start.Environment["FOCUS_UPGRADE_TEST_TARGET"] = target;
        // Track the alias before launching, so a partially successful command
        // is unlinked explicitly before recursive fixture cleanup.
        junctions.Add(alias);
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch isolated junction fixture creation."))
        {
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Synthetic junction creation did not finish.");
            }
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0) throw new InvalidOperationException("Junction fixture creation failed: " + error);
        }
        True(Directory.Exists(alias), "Junction fixture was not created.");
        True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "Alias must be a real reparse point, not a second ordinary directory.");
        Equal(DataDirectoryIdentity.Resolve(target), DataDirectoryIdentity.Resolve(alias));
        Equal(DataDirectoryIdentity.Resolve(target), DataDirectoryIdentity.Resolve(alias.ToUpperInvariant() + Path.DirectorySeparatorChar));
        var missingTarget = Path.Combine(target, "not-created", "nested");
        var missingAlias = Path.Combine(alias, "not-created", "nested");
        Equal(false, Directory.Exists(missingTarget));
        Equal(DataDirectoryIdentity.Resolve(missingTarget), DataDirectoryIdentity.Resolve(missingAlias));
        Equal(false, Directory.Exists(missingTarget));
        Equal(false, Directory.Exists(missingAlias));
    });

    Run("directory identity keeps separate synthetic directories distinct and rejects files", () =>
    {
        var directory = NewDirectory("distinct-identities");
        var first = Path.Combine(directory, "synthetic-normal-data");
        var second = Path.Combine(directory, "synthetic-test-data");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        True(DataDirectoryIdentity.Resolve(first) != DataDirectoryIdentity.Resolve(second), "Different directories unexpectedly share one identity.");
        Equal(DataDirectoryIdentity.Resolve(first), DataDirectoryIdentity.Resolve(Path.Combine(first, ".")));
        Equal(DataDirectoryIdentity.Resolve(first), DataDirectoryIdentity.Resolve(Path.Combine(second, "..", "synthetic-normal-data")));
        var file = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(file, "Synthetic file that must not become a data directory.");
        ThrowsAny(() => DataDirectoryIdentity.Resolve(file));
        ThrowsAny(() => DataDirectoryIdentity.Resolve(Path.Combine(file, "missing-child")));
        Equal("Synthetic file that must not become a data directory.", File.ReadAllText(file));
    });
}
finally
{
    // Only this run's freshly generated, absolute scratch path can be removed.
    var resolved = Path.GetFullPath(scratch);
    var prefix = tempParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "FocusGarden-upgrade-tests-";
    if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Directory.GetParent(resolved)?.FullName != tempParent)
        throw new InvalidOperationException("Refusing to clean up a path outside this test run's scratch directory.");
    try
    {
        foreach (var junction in junctions)
        {
            RequireScratchPath(junction);
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
        }
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
    catch (Exception ex) { results.Add(new TestResult("temporary fixture cleanup", false, 0, ex.ToString())); }
}

int passed = results.Count(result => result.Passed);
int failed = results.Count - passed;
string summary = $"Upgrade protection tests: {passed}/{results.Count} passed.";
Console.WriteLine(summary);
if (outputDirectory is not null)
{
    Directory.CreateDirectory(outputDirectory);
    File.WriteAllText(Path.Combine(outputDirectory, "upgrade-tests.json"), JsonSerializer.Serialize(new
    {
        fixtureSource = new { tag = "v0.1.0", commit = LegacyV010Fixture.SourceCommit, path = "src/FocusGarden.Core/FocusStore.cs" },
        scope = "Synthetic temporary SQLite fixtures only; no UI or real focus records accessed.",
        startedAt, finishedAt = DateTimeOffset.UtcNow, passed, failed, results
    }, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllLines(Path.Combine(outputDirectory, "upgrade-tests.txt"), results.Select(result => $"{(result.Passed ? "PASS" : "FAIL")} {result.Name} ({result.Milliseconds} ms){(result.Error is null ? "" : Environment.NewLine + result.Error)}").Append(summary));
}
return failed == 0 ? 0 : 1;

void Run(string name, Action body)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        body();
        results.Add(new TestResult(name, true, stopwatch.ElapsedMilliseconds, null));
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        results.Add(new TestResult(name, false, stopwatch.ElapsedMilliseconds, ex.ToString()));
        Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
    }
}

string NewDirectory(string name)
{
    var directory = Path.Combine(scratch, name);
    Directory.CreateDirectory(directory);
    return directory;
}

string FixtureDirectory(string name)
{
    var directory = NewDirectory(name);
    LegacyV010Fixture.Create(Database(directory));
    return directory;
}

void RequireScratchPath(string path)
{
    var resolved = Path.GetFullPath(path);
    var prefix = Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fixture operation would leave this run's synthetic scratch directory.");
}

static string Database(string directory) => Path.Combine(directory, "focus.db");
static string Marker(string directory) => Path.Combine(directory, "focus-data-version.json");
static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static string[] BackupFiles(string directory) => Directory.Exists(Path.Combine(directory, "backups")) ? Directory.GetFiles(Path.Combine(directory, "backups"), "*.db") : [];

static string RequiredBackup(string? backup, string dataDirectory, string version)
{
    True(!string.IsNullOrWhiteSpace(backup), "Expected a new backup path.");
    True(Path.IsPathFullyQualified(backup!), "Backup path must be absolute.");
    True(File.Exists(backup), "Returned backup does not exist.");
    Equal(Path.GetFullPath(Path.Combine(dataDirectory, "backups")), Path.GetDirectoryName(Path.GetFullPath(backup!)));
    True(Path.GetFileName(backup!).StartsWith("focus-before-" + version + "-", StringComparison.Ordinal), "Backup filename does not include the requested version.");
    Equal(".db", Path.GetExtension(backup!));
    return backup!;
}

static void CheckExactLegacyTotals(string db)
{
    Equal(4L, Scalar<long>(db, "SELECT COUNT(*) FROM sessions"));
    Equal((long)LegacyV010Fixture.TotalSeconds, Scalar<long>(db, "SELECT SUM(elapsed_seconds) FROM sessions"));
    Equal((long)LegacyV010Fixture.TotalSeconds, Scalar<long>(db, "SELECT SUM(seconds) FROM session_days"));
    foreach (var (day, seconds) in new[] { ("2024-02-28", 60), ("2024-02-29", 65), ("2025-12-31", 40), ("2026-01-01", 50), ("2026-02-28", 10), ("2026-03-01", 27), ("2026-10-27", 11) })
    {
        using var connection = LegacyV010Fixture.Open(db, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(seconds) FROM session_days WHERE day=$day";
        command.Parameters.AddWithValue("$day", day);
        Equal((long)seconds, (long)command.ExecuteScalar()!);
    }
    Equal(0L, Scalar<long>(db, "SELECT COUNT(*) FROM sessions s WHERE elapsed_seconds<>(SELECT COALESCE(SUM(seconds),0) FROM session_days d WHERE d.session_id=s.id)"));
}

static string Snapshot(string db) => string.Join("\n", new[]
{
    Query(db, "PRAGMA user_version"),
    Query(db, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name"),
    Query(db, "SELECT * FROM settings ORDER BY id"),
    Query(db, "SELECT * FROM sessions ORDER BY id"),
    Query(db, "SELECT * FROM session_days ORDER BY session_id,day"),
    Query(db, "SELECT * FROM sqlite_sequence ORDER BY name")
});

static string ImmutableSnapshot(string db) => string.Join("\n", new[]
{
    Query(db, "SELECT * FROM settings ORDER BY id"),
    Query(db, "SELECT id,task,started_at,planned_seconds,elapsed_seconds,last_checkpoint FROM sessions ORDER BY id"),
    Query(db, "SELECT * FROM session_days ORDER BY session_id,day"),
    Query(db, "SELECT * FROM sqlite_sequence ORDER BY name")
});

static string Query(string db, string sql)
{
    using var connection = LegacyV010Fixture.Open(db, readOnly: true);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var rows = new List<object?[]>();
    while (reader.Read())
    {
        var values = new object?[reader.FieldCount];
        for (int index = 0; index < reader.FieldCount; index++) values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        rows.Add(values);
    }
    return JsonSerializer.Serialize(rows);
}

static T Scalar<T>(string db, string sql)
{
    using var connection = LegacyV010Fixture.Open(db, readOnly: true);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return (T)command.ExecuteScalar()!;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ThrowsAny(Action body)
{
    try { body(); }
    catch (Exception) { return; }
    throw new InvalidOperationException("Expected preparation or store open to reject this fixture.");
}

internal sealed record TestResult(string Name, bool Passed, long Milliseconds, string? Error);
