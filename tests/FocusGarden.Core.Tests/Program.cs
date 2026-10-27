using FocusGarden.Core;
using Microsoft.Data.Sqlite;

var passed = 0;
var failed = 0;
var basePath = Path.Combine(Path.GetTempPath(), "FocusGarden-core-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(basePath);

void Run(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
}

void Throws<T>(Action body) where T : Exception
{
    try { body(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

DateTimeOffset Local(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
{
    var time = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    return new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time));
}

Run("settings survive reopening and normalize without executing input", () =>
{
    var db = Path.Combine(basePath, "settings.db");
    using (var store = new FocusStore(db))
    {
        Equal(25, store.LoadSettings().FocusMinutes);
        store.SaveSettings(new FocusSettings { FocusMinutes = 40, BreakMinutes = 8, Immersive = false,
            BlockedApps = ["Notepad.exe", "notepad"], BlockedDomains = ["BILIBILI.com.", "bilibili.com"] });
    }
    using var reopened = new FocusStore(db);
    var settings = reopened.LoadSettings();
    Equal(40, settings.FocusMinutes); Equal(8, settings.BreakMinutes); Equal(false, settings.Immersive);
    Equal("notepad", settings.BlockedApps.Single()); Equal("bilibili.com", settings.BlockedDomains.Single());
});

Run("normal completion persists exactly planned duration", () =>
{
    var db = Path.Combine(basePath, "completed.db");
    var start = Local(2026, 10, 27, 10);
    using (var store = new FocusStore(db))
    {
        var id = store.BeginSession("读书 '); DROP TABLE sessions;--", 60, start);
        store.AddProgress(id, 40, start.AddSeconds(40));
        Throws<InvalidOperationException>(() => store.EndSession(id, "completed", start.AddSeconds(40)));
        store.AddProgress(id, 25, start.AddSeconds(65));
        store.EndSession(id, "completed", start.AddSeconds(65));
        Equal(60, store.GetDailyTotals(2026)["2026-10-27"]);
        Throws<InvalidOperationException>(() => store.AddProgress(id, 1, start.AddSeconds(66)));
    }
    using var reopened = new FocusStore(db);
    var row = reopened.GetSessions().Single();
    Equal("completed", row.Status); Equal(60, row.ElapsedSeconds); Equal(60, row.PlannedSeconds);
    Equal("读书 '); DROP TABLE sessions;--", row.Task);
});

Run("midnight splits actual focus between natural days", () =>
{
    using var store = new FocusStore(":memory:");
    var start = Local(2026, 10, 27, 23, 59, 0);
    var id = store.BeginSession("跨午夜", 120, start);
    store.AddProgress(id, 120, start.AddSeconds(120));
    store.EndSession(id, "completed", start.AddSeconds(120));
    var days = store.GetDailyTotals(2026);
    Equal(60, days["2026-10-27"]); Equal(60, days["2026-10-28"]); Equal(120, days.Values.Sum());
});

Run("new year is allocated into separate heatmap years", () =>
{
    using var store = new FocusStore(":memory:");
    var start = Local(2026, 12, 31, 23, 59, 45);
    var id = store.BeginSession("跨年", 60, start);
    store.AddProgress(id, 60, start.AddSeconds(60));
    store.EndSession(id, "completed", start.AddSeconds(60));
    Equal(15, store.GetDailyTotals(2026)["2026-12-31"]);
    Equal(45, store.GetDailyTotals(2027)["2027-01-01"]);
});

Run("leap-day records appear and duration remains conserved", () =>
{
    using var store = new FocusStore(":memory:");
    var start = Local(2024, 2, 28, 23, 59, 50);
    var id = store.BeginSession("闰日", 30, start);
    store.AddProgress(id, 30, start.AddSeconds(30));
    store.EndSession(id, "emergency", start.AddSeconds(30));
    Equal(10, store.GetDailyTotals(2024)["2024-02-28"]);
    Equal(20, store.GetDailyTotals(2024)["2024-02-29"]);
});

Run("crash recovery records only checkpoint, never downtime", () =>
{
    var db = Path.Combine(basePath, "recover.db");
    var start = Local(2026, 10, 27, 12);
    using (var store = new FocusStore(db))
    {
        var id = store.BeginSession("恢复", 1500, start);
        store.AddProgress(id, 15, start.AddSeconds(15));
    }
    using var reopened = new FocusStore(db);
    reopened.RecoverInterrupted(start.AddDays(4));
    reopened.RecoverInterrupted(start.AddDays(5));
    var row = reopened.GetSessions().Single();
    Equal("interrupted", row.Status); Equal(15, row.ElapsedSeconds); Equal(start.AddSeconds(15), row.EndedAt!.Value);
    Equal(15, reopened.GetDailyTotals(2026).Values.Sum());
    reopened.BeginSession("恢复后新任务", 60, start.AddDays(5));
    Equal(2, reopened.GetSessions().Count);
});

Run("suspend gaps are never inferred by persistence", () =>
{
    using var store = new FocusStore(":memory:");
    var start = Local(2026, 10, 27, 13);
    var id = store.BeginSession("睡眠", 1500, start);
    store.AddProgress(id, 5, start.AddSeconds(5));
    store.AddProgress(id, 5, start.AddHours(2).AddSeconds(5));
    store.EndSession(id, "emergency", start.AddHours(2).AddSeconds(5));
    Equal(10, store.GetSessions().Single().ElapsedSeconds);
    Equal(10, store.GetDailyTotals(2026).Values.Sum());
});

Run("single running session and invalid statuses are rejected", () =>
{
    using var store = new FocusStore(":memory:");
    var start = Local(2026, 10, 27);
    var id = store.BeginSession("", 60, start);
    Equal("专注时光", store.GetSessions().Single().Task);
    Throws<SqliteException>(() => store.BeginSession("duplicate", 60, start));
    Throws<ArgumentException>(() => store.EndSession(id, "unknown", start));
    Throws<ArgumentOutOfRangeException>(() => store.AddProgress(id, -1, start));
    Throws<ArgumentException>(() => store.AddProgress(999, 1, start));
    store.EndSession(id, "emergency", start);
    Throws<InvalidOperationException>(() => store.EndSession(id, "emergency", start));
    Equal(0, store.GetDailyTotals(2026).Count);
});

Run("settings bounds preserve previous valid configuration", () =>
{
    using var store = new FocusStore(":memory:");
    store.SaveSettings(new FocusSettings { FocusMinutes = 50 });
    Throws<ArgumentException>(() => store.SaveSettings(new FocusSettings { FocusMinutes = 0 }));
    Throws<ArgumentException>(() => store.SaveSettings(new FocusSettings { BreakMinutes = 61 }));
    Throws<ArgumentException>(() => store.SaveSettings(new FocusSettings { BlockedApps = ["taskmgr.exe"] }));
    Throws<ArgumentException>(() => store.SaveSettings(new FocusSettings { BlockedDomains = ["https://example.com/path"] }));
    Equal(50, store.LoadSettings().FocusMinutes);
    Throws<ArgumentOutOfRangeException>(() => store.BeginSession("bad", 0, DateTimeOffset.Now));
    Throws<ArgumentException>(() => store.BeginSession(new string('x', 201), 60, DateTimeOffset.Now));
});

Run("domain rules reject IP, URL, local, wildcard, invalid syntax", () =>
{
    Equal("example.com", DomainRules.Normalize(" Example.COM. "));
    Equal("xn--fsqu00a.xn--0zwm56d", DomainRules.Normalize("例子.测试"));
    foreach (var value in new[] { "", "localhost", "127.0.0.1", "https://a.com", "a.com/path", "a.com:443", "*.a.com", "a_.com", "-bad.com", "a..com", "a.local", "foo.localhost", "a b.com" })
        Throws<ArgumentException>(() => DomainRules.Normalize(value));
});

Run("application rules reject recovery tools and filesystem paths", () =>
{
    Equal("msedge", AppRules.Normalize("MSEdge.EXE"));
    Equal("my app", AppRules.Normalize("My App.exe"));
    foreach (var value in new[] { "taskmgr.exe", "FocusGarden.exe", "explorer", "powershell", "cmd.exe", "C:\\app.exe", "../app", "*", "", ".." })
        Throws<ArgumentException>(() => AppRules.Normalize(value));
});

Run("empty and future years return zero data without fabricated entries", () =>
{
    using var store = new FocusStore(":memory:");
    Equal(0, store.GetSessions().Count); Equal(0, store.GetDailyTotals(2027).Count);
    Throws<ArgumentOutOfRangeException>(() => store.GetDailyTotals(0));
    Throws<ArgumentOutOfRangeException>(() => store.GetSessions(0));
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(basePath, true); } catch { }
return failed == 0 ? 0 : 1;
