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

void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }

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

Run("strict-focus choice survives reopening and can be disabled for the next session", () =>
{
    var db = Path.Combine(basePath, "strict-settings.db");
    using (var store = new FocusStore(db))
    {
        Equal(false, store.LoadSettings().StrictFocus);
        store.SaveSettings(new FocusSettings { FocusMinutes = 35, StrictFocus = true });
    }
    using (var reopened = new FocusStore(db))
    {
        Equal(true, reopened.LoadSettings().StrictFocus);
        Equal(35, reopened.LoadSettings().FocusMinutes);
        var saved = reopened.LoadSettings(); saved.StrictFocus = false;
        reopened.SaveSettings(saved);
    }
    using var final = new FocusStore(db);
    Equal(false, final.LoadSettings().StrictFocus);
    Equal(35, final.LoadSettings().FocusMinutes);
});

Run("legacy settings without strict-focus stay optional and preserve saved seconds", () =>
{
    var db = Path.Combine(basePath, "legacy-settings.db");
    long id;
    using (var store = new FocusStore(db))
    {
        var start = Local(2026, 10, 27, 9);
        id = store.BeginSession("legacy focus", 60, start);
        store.AddProgress(id, 17, start.AddSeconds(17));
        store.EndSession(id, "emergency", start.AddSeconds(17));
    }
    using (var connection = new SqliteConnection("Data Source=" + db))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(id,json) VALUES(1,$json)";
        command.Parameters.AddWithValue("$json", "{\"FocusMinutes\":40,\"BreakMinutes\":8,\"Immersive\":false,\"BlockedApps\":[],\"BlockedDomains\":[]}");
        command.ExecuteNonQuery();
    }
    using var reopened = new FocusStore(db);
    var old = reopened.LoadSettings();
    Equal(false, old.StrictFocus); Equal(40, old.FocusMinutes); Equal(false, old.Immersive);
    Equal(false, old.WhitelistEnabled); Equal(0, old.AllowedApps.Length);
    Equal(true, old.HideTaskbar); Equal(true, old.BlockDesktopShortcuts);
    old.StrictFocus = true; reopened.SaveSettings(old);
    Equal(true, reopened.LoadSettings().StrictFocus);
    Equal(id, reopened.GetSessions().Single().Id);
    Equal(17, reopened.GetSessions().Single().ElapsedSeconds);
    Equal(17, reopened.GetDailyTotals(2026).Values.Sum());
});

Run("whitelist preferences round-trip without changing focus rows or other restriction settings", () =>
{
    var db = Path.Combine(basePath, "whitelist-settings.db");
    string before;
    using (var store = new FocusStore(db))
    {
        var start = Local(2026, 10, 27, 9);
        var id = store.BeginSession("history before whitelist", 60, start);
        store.AddProgress(id, 17, start.AddSeconds(17));
        store.EndSession(id, "emergency", start.AddSeconds(17));
        before = System.Text.Json.JsonSerializer.Serialize(store.GetSessions());
        store.SaveSettings(new FocusSettings
        {
            FocusMinutes = 40, BreakMinutes = 8, Immersive = false, StrictFocus = true,
            WhitelistEnabled = true, AllowedApps = [" WINWORD.EXE ", "winword", "Codex.exe", "ChatGPT", "powershell.exe"],
            HideTaskbar = false, BlockDesktopShortcuts = false,
            BlockedApps = ["notepad.exe"], BlockedDomains = ["example.com"]
        });
    }
    using var reopened = new FocusStore(db);
    var settings = reopened.LoadSettings();
    Equal(true, settings.WhitelistEnabled); Equal(true, settings.StrictFocus);
    Equal(false, settings.HideTaskbar); Equal(false, settings.BlockDesktopShortcuts);
    Equal("winword,codex,chatgpt,powershell", string.Join(',', settings.AllowedApps));
    Equal("notepad", settings.BlockedApps.Single()); Equal("example.com", settings.BlockedDomains.Single());
    Equal(before, System.Text.Json.JsonSerializer.Serialize(reopened.GetSessions()));
    Equal(17, reopened.GetDailyTotals(2026).Values.Sum());
    settings.WhitelistEnabled = false; reopened.SaveSettings(settings);
    var disabled = reopened.LoadSettings();
    Equal(false, disabled.WhitelistEnabled); Equal(4, disabled.AllowedApps.Length);
});

Run("invalid active whitelist preserves the previous saved settings and permits a disabled empty list", () =>
{
    using var store = new FocusStore(":memory:");
    store.SaveSettings(new FocusSettings { FocusMinutes = 47, WhitelistEnabled = true, AllowedApps = ["winword.exe"] });
    foreach (var names in new string[][] { [], [" "], ["*.exe"], ["C:\\app.exe"], ["explorer"], ["svchost"], ["applicationframehost"], Enumerable.Repeat("notepad", 101).ToArray() })
        Throws<ArgumentException>(() => store.SaveSettings(new FocusSettings { WhitelistEnabled = true, AllowedApps = names }));
    Throws<ArgumentException>(() => store.SaveSettings(new FocusSettings { AllowedApps = null! }));
    var unchanged = store.LoadSettings();
    Equal(47, unchanged.FocusMinutes); Equal(true, unchanged.WhitelistEnabled); Equal("winword", unchanged.AllowedApps.Single());
    store.SaveSettings(new FocusSettings { WhitelistEnabled = false, AllowedApps = [] });
    Equal(0, store.LoadSettings().AllowedApps.Length);
});

Run("allowlist normalization selects concrete apps independently of blacklist recovery exemptions", () =>
{
    Equal("codex", AppRules.NormalizeAllowed(" Codex.EXE "));
    Equal("chatgpt", AppRules.NormalizeAllowed("ChatGPT"));
    Equal("powershell", AppRules.NormalizeAllowed("PowerShell.exe"));
    Equal("my app", AppRules.NormalizeAllowed("My App.exe"));
    Throws<ArgumentException>(() => AppRules.Normalize("powershell"));
    foreach (var name in new[] { "", "..", "*", "../app", "C:\\app.exe", "explorer.exe", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "svchost.exe", "applicationframehost", "runtimebroker", "dllhost", "conhost" })
        Throws<ArgumentException>(() => AppRules.NormalizeAllowed(name));
});

Run("whitelist policy matches exact names and exempts only its own process or true safety interfaces", () =>
{
    var policy = FocusApplicationPolicy.Create(new FocusSettings { WhitelistEnabled = true, AllowedApps = ["WinWord.EXE", "codex"] });
    Assert(policy.IsAllowed("WINWORD")); Assert(policy.IsAllowed("codex.exe"));
    foreach (var name in new[] { "winword-helper", "mycodex", "chatgpt", "chrome", "explorer", "systemsettings", "applicationframehost", "cmd", "powershell", "pwsh", "windowsterminal", "focusgarden" })
        Assert(!policy.IsAllowed(name));
    Assert(policy.IsAllowed("focusgarden", isOwnProcess: true));
    foreach (var name in new[] { "taskmgr", "logonui", "winlogon", "lockapp", "consent" })
    {
        Assert(policy.IsAllowed(name.ToUpperInvariant() + ".exe"));
        Assert(FocusApplicationPolicy.IsSystemSafetyApp(name));
        Assert(!FocusApplicationPolicy.IsSystemSafetyApp(name + "-helper"));
    }
    Assert(!FocusApplicationPolicy.IsSystemSafetyApp("explorer"));
});

Run("a running application policy is immutable despite later settings or list mutations", () =>
{
    var settings = new FocusSettings
    {
        WhitelistEnabled = true, AllowedApps = ["notepad.exe"],
        HideTaskbar = true, BlockDesktopShortcuts = true, BlockedApps = ["chrome"]
    };
    var policy = FocusApplicationPolicy.Create(settings);
    settings.AllowedApps[0] = "chrome"; settings.BlockedApps[0] = "notepad";
    settings.WhitelistEnabled = false; settings.HideTaskbar = false; settings.BlockDesktopShortcuts = false;
    Assert(policy.WhitelistEnabled && policy.HideTaskbar && policy.BlockDesktopShortcuts);
    Assert(policy.IsAllowed("notepad")); Assert(!policy.IsAllowed("chrome"));
    Throws<NotSupportedException>(() => ((IList<string>)policy.AllowedApps)[0] = "chrome");
    Equal("notepad", policy.AllowedApps.Single());
});

Run("desktop controls are inactive outside whitelist mode and empty enabled policies cannot start", () =>
{
    var policy = FocusApplicationPolicy.Create(new FocusSettings { BlockedApps = ["notepad.exe"] });
    Assert(!policy.WhitelistEnabled && !policy.HideTaskbar && !policy.BlockDesktopShortcuts);
    Assert(!policy.IsAllowed("notepad")); Assert(policy.IsAllowed("chrome")); Assert(policy.IsAllowed("powershell"));
    Throws<ArgumentException>(() => FocusApplicationPolicy.Create(new FocusSettings { WhitelistEnabled = true }));
    Throws<ArgumentException>(() => FocusApplicationPolicy.Create(new FocusSettings { WhitelistEnabled = true, AllowedApps = [" "] }));
    var explicitTerminal = FocusApplicationPolicy.Create(new FocusSettings { WhitelistEnabled = true, AllowedApps = ["powershell.exe"], HideTaskbar = false, BlockDesktopShortcuts = false });
    Assert(explicitTerminal.IsAllowed("powershell")); Assert(!explicitTerminal.IsAllowed("cmd"));
    Assert(!explicitTerminal.HideTaskbar && !explicitTerminal.BlockDesktopShortcuts);
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

Run("statistics aggregate all completions while materializing only twelve recent sessions", () =>
{
    using var store = new FocusStore(":memory:");
    var start = Local(2026, 6, 1);
    for (int i = 0; i < 25; i++)
    {
        var id = store.BeginSession("session " + i, 2, start.AddMinutes(i));
        store.AddProgress(id, 2, start.AddMinutes(i).AddSeconds(2));
        store.EndSession(id, i % 2 == 0 ? "completed" : "emergency", start.AddMinutes(i).AddSeconds(2));
    }
    var summary = store.GetStatistics(2026, start.LocalDateTime.Date);
    Equal(13, summary.CompletedCount); Equal(12, summary.Latest.Count); Equal(50, summary.TodaySeconds);
    Equal("session 24", summary.Latest[0].Task);
    Equal(0, store.GetStatistics(2025, start.LocalDateTime.Date).CompletedCount);
    Equal(50, store.GetStatistics(2025, start.LocalDateTime.Date).TodaySeconds);
});

Run("incremental day query includes its boundary and preserves all stored rows", () =>
{
    using var store = new FocusStore(":memory:");
    foreach (var day in new[] { Local(2025, 12, 31), Local(2026, 1, 1), Local(2026, 1, 2) })
    {
        var id = store.BeginSession("day", 60, day); store.AddProgress(id, 7, day.AddSeconds(7)); store.EndSession(id, "emergency", day.AddSeconds(7));
    }
    var since = store.GetDailyTotalsSince(new DateTime(2026, 1, 1));
    Equal(2, since.Count); Equal(7, since["2026-01-01"]); Equal(3, store.GetAllDailyTotals().Count);
});

Run("statistics count completion by local year across offset representations", () =>
{
    using var store = new FocusStore(":memory:");
    var boundary = Local(2027, 1, 1);
    foreach (var end in new[] { boundary.AddSeconds(-1), boundary, boundary.AddSeconds(1) })
    {
        var started = end.AddSeconds(-2).ToUniversalTime();
        var id = store.BeginSession("year boundary", 2, started);
        store.AddProgress(id, 2, end.ToUniversalTime());
        store.EndSession(id, "completed", end.ToUniversalTime());
    }
    Equal(1, store.GetStatistics(2026, boundary.LocalDateTime.Date).CompletedCount);
    Equal(2, store.GetStatistics(2027, boundary.LocalDateTime.Date).CompletedCount);
    Equal(6, store.GetAllDailyTotals().Values.Sum());
});

Run("serial background work preserves FIFO and continues after a failed operation", () =>
{
    var queue = new SerialWorkQueue();
    try
    {
        var caller = Environment.CurrentManagedThreadId;
        var values = new List<int>();
        var tasks = Enumerable.Range(0, 40).Select(i => queue.RunAsync(() => { Assert(Environment.CurrentManagedThreadId != caller); values.Add(i); })).ToArray();
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        Assert(values.SequenceEqual(Enumerable.Range(0, 40)));
        Throws<IOException>(() => queue.RunAsync(() => throw new IOException("synthetic")).GetAwaiter().GetResult());
        Equal(42, queue.RunAsync(() => 42).GetAwaiter().GetResult());
    }
    finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    Throws<ObjectDisposedException>(() => queue.RunAsync(() => { }).GetAwaiter().GetResult());
});

Run("bounded background queue rejects excess work without dropping accepted writes on shutdown", () =>
{
    var queue = new SerialWorkQueue(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var blocker = queue.RunAsync(() => { entered.Set(); release.Wait(); });
    var writes = new List<int>();
    try
    {
        Assert(entered.Wait(TimeSpan.FromSeconds(5)));
        var tasks = Enumerable.Range(0, 256).Select(i => queue.RunAsync(() => writes.Add(i))).ToArray();
        Throws<InvalidOperationException>(() => queue.RunAsync(() => writes.Add(999)).GetAwaiter().GetResult());
        var closing = queue.DisposeAsync().AsTask(); Assert(!closing.IsCompleted);
        release.Set(); Task.WhenAll(tasks.Append(blocker).Append(closing)).GetAwaiter().GetResult();
        Assert(writes.SequenceEqual(Enumerable.Range(0, 256)));
    }
    finally { release.Set(); queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(basePath, true); } catch { }
return failed == 0 ? 0 : 1;
