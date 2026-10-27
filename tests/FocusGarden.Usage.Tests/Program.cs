using FocusGarden.Core;
using FocusGarden.App;

var passed = 0;
var failed = 0;
var temporaryRoot = Path.Combine(Path.GetTempPath(), "FocusGarden-usage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
DateTimeOffset Local(int year, int month, int day, int hour = 12, int minute = 0, int second = 0)
{
    var date = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
}
var start = Local(2026, 10, 27);
AppUsageSample S(double mono, string? app = "editor.exe", double idle = 0, bool enabled = true, bool locked = false, DateTimeOffset? at = null)
    => new(app, mono, at ?? start.AddSeconds(mono), idle, enabled, locked);
int Seconds(IEnumerable<AppUsageCredit> credits) => credits.Sum(c => c.Seconds);

Run("fractional active samples count once both endpoints agree", () =>
{
    var a = new UsageAccumulator(); Equal(0, Seconds(a.Sample(S(0))));
    var total = 0;
    for (var i = 1; i <= 20; i++) total += Seconds(a.Sample(S(i * .1)));
    Equal(2, total);
});
Run("switch boundaries are never attributed to either application", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0));
    Equal(0, Seconds(a.Sample(S(1, "browser.exe"))));
    var credits = a.Sample(S(2, "browser.exe")); Equal(1, Seconds(credits)); Equal("browser.exe", credits.Single().AppName);
    Equal(0, Seconds(a.Sample(S(3, "editor.exe"))));
    Equal(1, Seconds(a.Sample(S(4, "editor.exe"))));
});
Run("60 second idle cutoff and recovery both exclude uncertain intervals", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0, idle:59));
    Equal(0, Seconds(a.Sample(S(1, idle:60))));
    Equal(0, Seconds(a.Sample(S(2, idle:90))));
    Equal(0, Seconds(a.Sample(S(3, idle:0))));
    Equal(1, Seconds(a.Sample(S(4, idle:0))));
});
Run("locked and disabled samples never count or backfill on resume", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0));
    Equal(0, Seconds(a.Sample(S(1, locked:true)))); Equal(0, Seconds(a.Sample(S(2))));
    Equal(1, Seconds(a.Sample(S(3))));
    Equal(0, Seconds(a.Sample(S(4, enabled:false)))); Equal(0, Seconds(a.Sample(S(5))));
    Equal(1, Seconds(a.Sample(S(6))));
});
Run("three second and sleep gaps are skipped with a fresh baseline", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0));
    Equal(0, Seconds(a.Sample(S(3)))); Equal(1, Seconds(a.Sample(S(4))));
    Equal(0, Seconds(a.Sample(S(3604)))); Equal(1, Seconds(a.Sample(S(3605))));
    Equal(0, Seconds(a.Sample(S(3604))));
});
Run("missing foreground and explicit baseline resets cannot infer usage", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0));
    Equal(0, Seconds(a.Sample(S(1, app:null)))); Equal(0, Seconds(a.Sample(S(2))));
    a.ResetBaseline(); Equal(0, Seconds(a.Sample(S(3)))); Equal(1, Seconds(a.Sample(S(4))));
});
Run("fractional samples split at midnight before integer accumulation", () =>
{
    var midnight = Local(2026, 10, 28, 0);
    var a = new UsageAccumulator(); a.Sample(S(0, at:midnight.AddSeconds(-1.25)));
    var credits = new List<AppUsageCredit>();
    for (int i = 1; i <= 10; i++) credits.AddRange(a.Sample(S(i * .25, at:midnight.AddSeconds(-1.25 + i * .25))));
    Equal(1, credits.Where(c => c.Day == midnight.Date.AddDays(-1)).Sum(c => c.Seconds));
    Equal(1, credits.Where(c => c.Day == midnight.Date).Sum(c => c.Seconds));
    Equal(2, Seconds(credits));
});
Run("app fractions stay with their own app through valid disjoint visits", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0)); a.Sample(S(.5));
    a.Sample(S(1, "browser")); a.Sample(S(1.5, "browser")); a.Sample(S(2));
    var credit = a.Sample(S(2.5)); Equal("editor.exe", credit.Single().AppName); Equal(1, Seconds(credit));
});
Run("wall clock jumps do not add duration", () =>
{
    var a = new UsageAccumulator(); a.Sample(S(0));
    var credit = a.Sample(S(1, at:start.AddHours(8))); Equal(1, Seconds(credit));
});
Run("SQLite totals persist, normalize and order across reopen", () =>
{
    var path = Path.Combine(temporaryRoot, "persistent.db");
    using (var store = new AppUsageStore(path))
    {
        store.AddUsage("Editor.EXE", 25, start); store.AddUsage("editor", 15, start);
        store.AddUsage("Microsoft.Photos.exe", 50, start);
    }
    using var reopened = new AppUsageStore(path);
    var rows = reopened.GetDailyUsage(start.Date); Equal(2, rows.Count);
    Equal("microsoft.photos.exe", rows[0].AppName); Equal(50, rows[0].Seconds); Equal(40, rows[1].Seconds);
    Equal(0, reopened.GetDailyUsage(start.Date.AddDays(-1)).Count);
});
Run("SQLite known intervals split local midnight and new year", () =>
{
    using var store = new AppUsageStore(":memory:");
    var end = Local(2027, 1, 1, 0, 0, 20); store.AddUsage("editor.exe", 60, end);
    Equal(40, store.GetDailyUsage(new DateTime(2026,12,31)).Single().Seconds);
    Equal(20, store.GetDailyUsage(new DateTime(2027,1,1)).Single().Seconds);
});
Run("usage table coexists with focus sessions without schema mutation", () =>
{
    var path = Path.Combine(temporaryRoot, "shared.db");
    using var focus = new FocusStore(path); using var usage = new AppUsageStore(path);
    var id = focus.BeginSession("keep session", 60, start); focus.AddProgress(id, 5, start.AddSeconds(5));
    usage.AddUsage("focusgarden.exe", 3, start.AddSeconds(3));
    focus.EndSession(id, "emergency", start.AddSeconds(5));
    Equal(5, focus.GetSessions().Single().ElapsedSeconds); Equal(3, usage.GetDailyUsage(start.Date).Single().Seconds);
});
Run("invalid batches never partially persist", () =>
{
    using var store = new AppUsageStore(":memory:");
    Throws<ArgumentException>(() => store.AddCredits([new("editor", start.Date, 2), new("C:\\secret.exe", start.Date, 3)]));
    Equal(0, store.GetDailyUsage(start.Date).Count);
    Throws<ArgumentOutOfRangeException>(() => store.AddUsage("app", -1, start));
    Throws<ArgumentException>(() => store.AddUsage("app\nname", 1, start));
    store.AddUsage("app", 0, start); Equal(0, store.GetDailyUsage(start.Date).Count);
});
Run("invalid monotonic and idle inputs are rejected", () =>
{
    var a = new UsageAccumulator(); Throws<ArgumentException>(() => a.Sample(S(double.NaN)));
    Throws<ArgumentException>(() => a.Sample(S(double.PositiveInfinity)));
    Throws<ArgumentException>(() => a.Sample(S(0, idle:-1)));
});
Run("tracker disabled and locked states are exposed without accessing foreground", () =>
{
    using var store = new AppUsageStore(":memory:"); var tracker = new AppUsageTracker(store);
    tracker.Enabled = false; tracker.Sample(); Equal("已暂停统计", tracker.StateText);
    tracker.IsSessionLocked = true; tracker.Enabled = true; tracker.Sample(); Equal("系统已锁定，暂停统计", tracker.StateText);
    tracker.Flush(); Equal(0, tracker.GetDailyUsage(start.Date).Count); Equal<string?>(null, tracker.LastError);
});

var resolvedTestRoot = Path.GetFullPath(temporaryRoot);
var resolvedTempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!resolvedTestRoot.StartsWith(resolvedTempParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolvedTestRoot).StartsWith("FocusGarden-usage-", StringComparison.Ordinal))
    throw new InvalidOperationException("Refusing to clean a path outside the temporary test directory.");
try { Directory.Delete(resolvedTestRoot, recursive:true); } catch { }
Console.WriteLine($"RESULT {passed} passed / {failed} failed");
return failed == 0 ? 0 : 1;
