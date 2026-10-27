using System.Globalization;
using FocusGarden.Core;
using Microsoft.Data.Sqlite;

var passed = 0;
var failed = 0;
var basePath = Path.Combine(Path.GetTempPath(), "FocusGarden-planet-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(basePath);
var sourceA = Path.Combine(basePath, "source-a");
var sourceB = Path.Combine(basePath, "source-b");
var start = Local(2026, 1, 31, 10);
var emptyFocus = new Dictionary<string, int>();
var emptyTokens = new Dictionary<string, long>();

void Run(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
}

void True(bool value, string message = "Expected true")
{
    if (!value) throw new Exception(message);
}

void Throws<T>(Action body) where T : Exception
{
    try { body(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

DateTimeOffset Local(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
{
    var date = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
}

string Day(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
Dictionary<string, int> Focus(int seconds, DateTimeOffset? date = null) => new() { [Day(date ?? start)] = seconds };
Dictionary<string, long> Tokens(long tokens, DateTimeOffset? date = null) => new() { [Day(date ?? start)] = tokens };
PlanetGrowthStore NewStore(DateTimeOffset? at = null)
{
    var store = new PlanetGrowthStore(":memory:");
    store.Initialize(at ?? start, emptyFocus);
    store.GetTokenPeriodStart(sourceA, at ?? start);
    return store;
}

Run("point formula, flooring, diminishing returns and independent contributions", () =>
{
    Equal(new PlanetDailyPoints(0, 0, 0), PlanetGrowthRules.Calculate(59, 0));
    Equal(new PlanetDailyPoints(25, 0, 0), PlanetGrowthRules.Calculate(1500, 0));
    Equal(new PlanetDailyPoints(0, 15, 0), PlanetGrowthRules.Calculate(0, 100000));
    Equal(new PlanetDailyPoints(25, 15, 8), PlanetGrowthRules.Calculate(1500, 100000));
    Equal(new PlanetDailyPoints(60, 30, 20), PlanetGrowthRules.Calculate(3600, 300000));
    Equal(new PlanetDailyPoints(0, 14, 0), PlanetGrowthRules.Calculate(0, 99999));
    Equal(new PlanetDailyPoints(0, 60, 0), PlanetGrowthRules.Calculate(0, 1500000));
});

Run("daily caps and extreme or negative input stay bounded", () =>
{
    Equal(200, PlanetGrowthRules.Calculate(int.MaxValue, long.MaxValue).TotalGrowth);
    Equal(0, PlanetGrowthRules.Calculate(int.MinValue, long.MinValue).TotalGrowth);
    Equal(120, PlanetGrowthRules.Calculate(int.MaxValue, 0).TotalGrowth);
    Equal(60, PlanetGrowthRules.Calculate(0, long.MaxValue).TotalGrowth);
});

Run("activation excludes previous days and existing focus on activation day", () =>
{
    using var store = new PlanetGrowthStore(":memory:");
    var initial = Focus(1800);
    initial[Day(start.AddDays(-1))] = 7200;
    store.Initialize(start, initial);
    Equal(0L, store.Update(start, initial, emptyTokens).TotalGrowth);
    initial[Day(start)] += 1500;
    var snapshot = store.Update(start.AddMinutes(25), initial, emptyTokens);
    Equal(25L, snapshot.TotalGrowth);
    Equal(1500, snapshot.TodayFocusSeconds);
    Equal(0, snapshot.Level);
});

Run("repeated initialization preserves baseline name and seed", () =>
{
    using var store = new PlanetGrowthStore(":memory:");
    store.Initialize(start, Focus(600));
    store.SetName("启明星");
    var first = store.GetSnapshot(start);
    store.Initialize(start.AddDays(20), Focus(5000));
    var updated = store.Update(start, Focus(1200), emptyTokens);
    Equal(10L, updated.TotalGrowth);
    Equal(start, updated.ActivatedAt);
    Equal(first.Seed, updated.Seed);
    Equal("启明星", updated.Name);
    Equal(1, store.ListPlanets().Count);
});

Run("first Token binding starts at activation and equivalent source path is stable", () =>
{
    using var store = new PlanetGrowthStore(":memory:");
    store.Initialize(start, emptyFocus);
    Equal(start, store.GetTokenPeriodStart(sourceA, start.AddDays(2)));
    Equal(start, store.GetTokenPeriodStart(sourceA.ToUpperInvariant() + Path.DirectorySeparatorChar, start.AddDays(3)));
});

Run("repeated refresh does not duplicate points", () =>
{
    using var store = NewStore();
    var first = store.Update(start, Focus(1500), Tokens(100000));
    Equal(48L, first.NewGrowth);
    for (var i = 0; i < 10; i++)
    {
        var repeated = store.Update(start.AddSeconds(i), Focus(1500), Tokens(100000));
        Equal(0L, repeated.NewGrowth);
        Equal(48L, repeated.TotalGrowth);
        Equal(48, repeated.TodayPlanetGrowth);
    }
});

Run("focus rollback disappearance and recovery preserve high water", () =>
{
    using var store = NewStore();
    Equal(25L, store.Update(start, Focus(1500), emptyTokens).NewGrowth);
    Equal(0L, store.Update(start, Focus(300), emptyTokens).NewGrowth);
    Equal(1500, store.Update(start, emptyFocus, emptyTokens).TodayFocusSeconds);
    Equal(0L, store.Update(start, Focus(1500), emptyTokens).NewGrowth);
    Equal(1L, store.Update(start, Focus(1560), emptyTokens).NewGrowth);
});

Run("Token rollback disappearance and recovery preserve high water", () =>
{
    using var store = NewStore();
    Equal(15L, store.Update(start, emptyFocus, Tokens(100000)).NewGrowth);
    Equal(0L, store.Update(start, emptyFocus, Tokens(20000)).NewGrowth);
    Equal(100000L, store.Update(start, emptyFocus, emptyTokens).TodayTokens);
    Equal(0L, store.Update(start, emptyFocus, Tokens(100000)).NewGrowth);
    var more = store.Update(start, emptyFocus, Tokens(300000));
    Equal(15L, more.NewGrowth);
    Equal(300000L, more.TodayTokens);
});

Run("source switches and switching back begin fresh observation periods", () =>
{
    using var store = NewStore();
    store.Update(start, emptyFocus, Tokens(100000));
    Equal(start.AddHours(1), store.GetTokenPeriodStart(sourceB, start.AddHours(1)));
    var second = store.Update(start.AddHours(1), emptyFocus, Tokens(100000));
    Equal(200000L, second.TodayTokens);
    Equal(23L, second.TotalGrowth);
    Equal(start.AddHours(2), store.GetTokenPeriodStart(sourceA, start.AddHours(2)));
    var third = store.Update(start.AddHours(2), emptyFocus, Tokens(100000));
    Equal(300000L, third.TodayTokens);
    Equal(30L, third.TotalGrowth);
    Equal(0L, store.Update(start.AddHours(2), emptyFocus, Tokens(100000)).NewGrowth);
});

Run("previous dates are excluded after Token source switch", () =>
{
    using var store = NewStore();
    store.Update(start, emptyFocus, Tokens(100000));
    var tomorrow = start.AddDays(1);
    store.GetTokenPeriodStart(sourceB, tomorrow);
    var input = Tokens(long.MaxValue);
    input[Day(tomorrow)] = 100000;
    var result = store.Update(tomorrow, emptyFocus, input);
    Equal(30L, result.TotalGrowth);
    Equal(100000L, result.TodayTokens);
});

Run("clock rollback never reopens an old Token period", () =>
{
    using var store = NewStore();
    var cutoff = start.AddDays(3);
    Equal(cutoff, store.GetTokenPeriodStart(sourceB, cutoff));
    Equal(cutoff, store.GetTokenPeriodStart(sourceA, start.AddDays(1)));
    Equal(0L, store.Update(start.AddDays(1), emptyFocus, Tokens(100000, start.AddDays(1))).NewGrowth);
});

Run("midnight resets daily cap while permanent growth remains", () =>
{
    var late = Local(2026, 5, 2, 23, 59);
    using var store = NewStore(late);
    Equal(200L, store.Update(late, Focus(7200, late), Tokens(1500000, late)).TotalGrowth);
    var next = late.AddMinutes(2);
    var snapshot = store.Update(next, Focus(60, next), Tokens(100000, next));
    Equal(16, snapshot.TodayGrowth);
    Equal(216L, snapshot.TotalGrowth);
});

Run("cross-year history remains credited without replay", () =>
{
    var late = Local(2026, 12, 31, 23, 59);
    using var store = NewStore(late);
    store.Update(late, Focus(1500, late), Tokens(100000, late));
    var next = late.AddMinutes(2);
    var focus = Focus(1500, late);
    focus[Day(next)] = 600;
    var snapshot = store.Update(next, focus, Tokens(0, next));
    Equal(58L, snapshot.TotalGrowth);
    Equal(10, snapshot.TodayGrowth);
    Equal(0L, store.Update(next, focus, Tokens(0, next)).NewGrowth);
});

Run("persistent restart keeps baseline source observations seed and credits", () =>
{
    var db = Path.Combine(basePath, "reopen.db");
    PlanetSnapshot first;
    using (var store = new PlanetGrowthStore(db))
    {
        store.Initialize(start, Focus(600));
        store.GetTokenPeriodStart(sourceA, start);
        store.SetName("重启之后");
        first = store.Update(start, Focus(2100), Tokens(100000));
    }
    using var reopened = new PlanetGrowthStore(db);
    reopened.Initialize(start.AddDays(1), Focus(2100));
    Equal(start, reopened.GetTokenPeriodStart(sourceA, start.AddDays(1)));
    var again = reopened.Update(start, Focus(2100), Tokens(100000));
    Equal(first.TotalGrowth, again.TotalGrowth);
    Equal(first.Seed, again.Seed);
    Equal(first.PlanetId, again.PlanetId);
    Equal("重启之后", again.Name);
    Equal(0L, again.NewGrowth);
});

Run("levels require both growth and calendar age", () =>
{
    Equal(0, PlanetGrowthRules.GetLevel(149, start, start.AddYears(10)));
    Equal(1, PlanetGrowthRules.GetLevel(150, start, start));
    Equal(1, PlanetGrowthRules.GetLevel(9000, start, start));
    Equal(2, PlanetGrowthRules.GetLevel(9000, start, start.AddMonths(1)));
    Equal(3, PlanetGrowthRules.GetLevel(9000, start, start.AddMonths(2)));
    Equal(4, PlanetGrowthRules.GetLevel(9000, start, start.AddMonths(3)));
    Equal(4, PlanetGrowthRules.GetLevel(9000, start, start.AddMonths(5).AddTicks(-1)));
    Equal(5, PlanetGrowthRules.GetLevel(9000, start, start.AddMonths(5)));
    Equal(5, PlanetGrowthRules.GetLevel(long.MaxValue, start, start.AddMonths(6)));
});

Run("maximum level retains growth and has no next threshold", () =>
{
    using var store = NewStore();
    var focus = new Dictionary<string, int>();
    var tokens = new Dictionary<string, long>();
    for (var i = 0; i < 50; i++)
    {
        focus[Day(start.AddDays(i))] = 7200;
        tokens[Day(start.AddDays(i))] = 1500000;
    }
    var result = store.Update(start.AddMonths(5), focus, tokens);
    Equal(10000L, result.TotalGrowth);
    Equal(5, result.Level);
    Equal("星际绿洲", result.LevelName);
    Equal<long?>(null, result.NextLevelAt);
    Equal<DateTimeOffset?>(null, result.NextLevelAvailableAt);
    Equal("天文台与极光", result.LatestUnlock);
});

Run("name validation accepts visible Unicode and rejects invalid input", () =>
{
    using var store = NewStore();
    store.SetName("  小星球🌍  ");
    Equal("小星球🌍", store.GetSnapshot(start).Name);
    Throws<ArgumentException>(() => store.SetName(" "));
    Throws<ArgumentException>(() => store.SetName(new string('星', 21)));
    Throws<ArgumentException>(() => store.SetName("星\n球"));
    Equal("小星球🌍", store.GetSnapshot(start).Name);
    var emojiName = string.Concat(Enumerable.Repeat("🌍", 20));
    store.SetName(emojiName);
    Equal(emojiName, store.GetSnapshot(start).Name);
});

Run("invalid future preactivation and negative observations do not award", () =>
{
    using var store = NewStore();
    var focus = new Dictionary<string, int>
    {
        [Day(start.AddDays(-1))] = int.MaxValue, [Day(start.AddDays(1))] = int.MaxValue,
        ["2026-02-30"] = int.MaxValue, ["2026-1-31"] = int.MaxValue, ["nonsense"] = int.MaxValue,
        [Day(start)] = -1
    };
    var tokens = focus.ToDictionary(x => x.Key, x => (long)x.Value);
    var result = store.Update(start, focus, tokens);
    Equal(0L, result.TotalGrowth);
    Equal(0, result.TodayFocusSeconds);
    Equal(0L, result.TodayTokens);
});

Run("weather is deterministic within a local three-hour slot and across reopening", () =>
{
    var db = Path.Combine(basePath, "weather.db");
    PlanetSnapshot first;
    using (var store = new PlanetGrowthStore(db))
    {
        store.Initialize(start, emptyFocus);
        first = store.GetSnapshot(Local(2026, 1, 31, 0, 1));
        var same = store.GetSnapshot(Local(2026, 1, 31, 2, 59));
        Equal(first.Weather, same.Weather);
        Equal(true, same.IsNight);
        Equal(false, store.GetSnapshot(Local(2026, 1, 31, 12)).IsNight);
        Equal(true, store.GetSnapshot(Local(2026, 1, 31, 19)).IsNight);
        True(new[] { "clear", "cloudy", "rain" }.Contains(first.Weather));
    }
    using var reopened = new PlanetGrowthStore(db);
    Equal(first.Weather, reopened.GetSnapshot(Local(2026, 1, 31, 0, 1)).Weather);
});

Run("aurora is possible only at maximum level at night", () =>
{
    using var store = NewStore();
    var focus = Enumerable.Range(0, 50).ToDictionary(i => Day(start.AddDays(i)), _ => 7200);
    var tokens = Enumerable.Range(0, 50).ToDictionary(i => Day(start.AddDays(i)), _ => 1500000L);
    var mature = start.AddMonths(5);
    store.Update(mature, focus, tokens);
    var seenAurora = false;
    for (var i = 0; i < 60; i++)
    {
        var day = mature.AddDays(i);
        var night = Local(day.Year, day.Month, day.Day, 22);
        var noon = Local(day.Year, day.Month, day.Day, 12);
        seenAurora |= store.GetSnapshot(night).Weather == "aurora";
        True(store.GetSnapshot(noon).Weather != "aurora");
    }
    True(seenAurora, "Deterministic 60-night sample should contain an aurora");
});

Run("transaction failure rolls back observations awards and per-planet credits", () =>
{
    var db = Path.Combine(basePath, "atomic.db");
    using var store = new PlanetGrowthStore(db);
    store.Initialize(start, emptyFocus);
    store.GetTokenPeriodStart(sourceA, start);
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TRIGGER fail_credit BEFORE INSERT ON planet_growth_credits BEGIN SELECT RAISE(ABORT,'injected failure'); END";
    command.ExecuteNonQuery();
    Throws<SqliteException>(() => store.Update(start, Focus(1500), Tokens(100000)));
    Equal(0L, store.GetSnapshot(start).TotalGrowth);
    Equal(0L, store.GetSnapshot(start).TodayTokens);
    Equal(0, store.GetSnapshot(start).TodayFocusSeconds);
    command.CommandText = "DROP TRIGGER fail_credit";
    command.ExecuteNonQuery();
    Equal(48L, store.Update(start, Focus(1500), Tokens(100000)).NewGrowth);
});

Run("unbound Token update fails atomically and is recoverable", () =>
{
    using var store = new PlanetGrowthStore(":memory:");
    store.Initialize(start, emptyFocus);
    Throws<InvalidOperationException>(() => store.Update(start, Focus(1500), Tokens(100000)));
    Equal(0, store.GetSnapshot(start).TodayFocusSeconds);
    store.GetTokenPeriodStart(sourceA, start);
    Equal(48L, store.Update(start, Focus(1500), Tokens(100000)).NewGrowth);
});

Run("Token accumulation saturates safely across multiple sources", () =>
{
    using var store = NewStore();
    store.Update(start, emptyFocus, Tokens(long.MaxValue - 10));
    store.GetTokenPeriodStart(sourceB, start.AddSeconds(1));
    var result = store.Update(start.AddSeconds(1), emptyFocus, Tokens(1000));
    Equal(long.MaxValue, result.TodayTokens);
    Equal(60L, result.TotalGrowth);
});

Run("six natural months are required and low-growth planets may explore", () =>
{
    using var store = NewStore();
    var due = start.AddMonths(6);
    Equal(due, store.GetSnapshot(start).CycleEndsAt);
    Equal(false, store.GetSnapshot(due.AddTicks(-1)).CanExplore);
    Throws<InvalidOperationException>(() => store.ExploreNewPlanet(due.AddTicks(-1)));
    Equal(true, store.GetSnapshot(due).CanExplore);
    var next = store.ExploreNewPlanet(due);
    Equal(0L, next.TotalGrowth);
    Equal(0, next.Level);
    Equal(due, next.ActivatedAt);
    Equal(0, next.AgeDays);
    Equal(2, store.ListPlanets().Count);
});

Run("calendar month end and leap-day anniversary follow AddMonths", () =>
{
    var august = Local(2027, 8, 31, 18);
    using var store = NewStore(august);
    var due = Local(2028, 2, 29, 18);
    Equal(due, store.GetSnapshot(august).CycleEndsAt);
    Equal(false, store.GetSnapshot(due.AddSeconds(-1)).CanExplore);
    Equal(true, store.GetSnapshot(due).CanExplore);
    Equal(1, PlanetGrowthRules.GetLevel(1200, august, Local(2027, 9, 30, 17, 59)));
    Equal(2, PlanetGrowthRules.GetLevel(1200, august, Local(2027, 9, 30, 18)));
});

Run("old planets persist and each planet can open only one successor", () =>
{
    using var store = NewStore();
    store.SetName("第一家园");
    var original = store.Update(start, Focus(1500), Tokens(100000));
    var due = start.AddMonths(6);
    var next = store.ExploreNewPlanet(due);
    True(next.PlanetId != original.PlanetId);
    store.SetName("新大陆");
    store.SelectPlanet(original.PlanetId);
    var old = store.GetSnapshot(due);
    Equal("第一家园", old.Name);
    Equal(48L, old.TotalGrowth);
    Equal(original.Seed, old.Seed);
    Equal(false, old.CanExplore);
    Throws<InvalidOperationException>(() => store.ExploreNewPlanet(due.AddMonths(6)));
    store.SelectPlanet(next.PlanetId);
    Equal("新大陆", store.GetSnapshot(due).Name);
    Equal(true, store.GetSnapshot(due.AddMonths(6)).CanExplore);
    store.ExploreNewPlanet(due.AddMonths(6));
    Equal(3, store.ListPlanets().Count);
});

Run("switching planets only routes newly earned deltas and shares daily cap", () =>
{
    using var store = NewStore();
    var originalId = store.GetSnapshot(start).PlanetId;
    var due = start.AddMonths(6);
    store.Update(due, Focus(1500, due), Tokens(100000, due));
    var next = store.ExploreNewPlanet(due);
    Equal(start, store.GetTokenPeriodStart(sourceA, due));
    var repeated = store.Update(due, Focus(1500, due), Tokens(100000, due));
    Equal(0L, repeated.TotalGrowth);
    Equal(48, repeated.TodayGrowth);
    Equal(0, repeated.TodayPlanetGrowth);
    var increased = store.Update(due, Focus(1800, due), Tokens(100000, due));
    Equal(7L, increased.NewGrowth);
    Equal(7L, increased.TotalGrowth);
    store.SelectPlanet(originalId);
    var capped = store.Update(due, Focus(7200, due), Tokens(1500000, due));
    Equal(145L, capped.NewGrowth);
    Equal(193L, capped.TotalGrowth);
    Equal(200, capped.TodayGrowth);
    store.SelectPlanet(next.PlanetId);
    Equal(0L, store.Update(due, Focus(7200, due), Tokens(1500000, due)).NewGrowth);
    Equal(200L, store.ListPlanets().Sum(x => x.TotalGrowth));
});

Run("multi-planet selection names and no-replay survive restart", () =>
{
    var db = Path.Combine(basePath, "multi.db");
    var due = start.AddMonths(6);
    long originalId;
    long nextId;
    using (var store = new PlanetGrowthStore(db))
    {
        store.Initialize(start, emptyFocus);
        store.GetTokenPeriodStart(sourceA, start);
        originalId = store.Update(due, Focus(1500, due), Tokens(100000, due)).PlanetId;
        nextId = store.ExploreNewPlanet(due).PlanetId;
        store.SetName("第二颗");
    }
    using var reopened = new PlanetGrowthStore(db);
    Equal(nextId, reopened.GetSnapshot(due).PlanetId);
    Equal("第二颗", reopened.GetSnapshot(due).Name);
    Equal(0L, reopened.Update(due, Focus(1500, due), Tokens(100000, due)).NewGrowth);
    Equal(2, reopened.ListPlanets().Count);
    Equal(1, reopened.ListPlanets().Count(x => x.IsActive));
    reopened.SelectPlanet(originalId);
    Equal(false, reopened.GetSnapshot(due).CanExplore);
    Equal(48L, reopened.GetSnapshot(due).TotalGrowth);
});

Run("invalid selection and disposed or uninitialized operations are rejected", () =>
{
    using var store = new PlanetGrowthStore(":memory:");
    Throws<InvalidOperationException>(() => store.GetSnapshot(start));
    Throws<InvalidOperationException>(() => store.ListPlanets());
    Throws<InvalidOperationException>(() => store.SetName("未创建"));
    Throws<InvalidOperationException>(() => store.Update(start, emptyFocus, emptyTokens));
    store.Initialize(start, emptyFocus);
    var id = store.GetSnapshot(start).PlanetId;
    Throws<ArgumentException>(() => store.SelectPlanet(-1));
    Equal(id, store.GetSnapshot(start).PlanetId);
    store.Dispose();
    Throws<ObjectDisposedException>(() => store.GetSnapshot(start));
    Throws<ObjectDisposedException>(() => store.Initialize(start, emptyFocus));
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(basePath, true); } catch { }
return failed == 0 ? 0 : 1;
