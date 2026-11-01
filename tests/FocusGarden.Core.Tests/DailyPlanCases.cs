using FocusGarden.Core;

internal static class DailyPlanCases
{
    internal static void Run(string root, Action<string, Action> test)
    {
        DailyFocusPlan Plan() => new() { Enabled = true, Start = "09:00", End = "22:00",
            Slots = [new() { Start = "09:00", End = "12:00", Apps = "WINWORD.exe; notepad; WINWORD", Label = "写作" }, new() { Start = "14:00", End = "18:00", Apps = "msedge" }] };
        DateTimeOffset Time(int hour, int minute = 0) => new(2026, 11, 2, hour, minute, 0, TimeSpan.FromHours(8));
        test("daily plan boundaries are start-inclusive end-exclusive and unplanned gaps are explicit", () =>
        {
            var plan = Plan().Snapshot();
            Check(plan.WindowAt(Time(8, 59)) == null && plan.WindowAt(Time(9)) != null && plan.WindowAt(Time(22)) == null);
            var window = plan.WindowAt(Time(9))!;
            Check(plan.SlotAt(Time(9), window)!.AppNames().SequenceEqual(new[] { "winword", "notepad" }));
            Check(plan.SlotAt(Time(12), window) == null && plan.SlotAt(Time(14), window)!.AppNames().Single() == "msedge");
            Check(!plan.CountWholePeriod && !plan.AllowUnplannedApps);
        });
        test("daily overnight slots share the previous evening's suppression key", () =>
        {
            var plan = new DailyFocusPlan { Start = "22:00", End = "06:00", Slots = [new() { Start = "23:00", End = "02:00", Apps = "notepad" }] }.Snapshot();
            var night = plan.WindowAt(Time(23))!; var morning = plan.WindowAt(Time(1).AddDays(1))!;
            Check(night.Key == morning.Key && night.End == Time(6).AddDays(1));
            Check(plan.SlotAt(Time(1).AddDays(1), night) != null && plan.SlotAt(Time(2).AddDays(1), night) == null);
            Check(plan.WindowAt(Time(6).AddDays(1)) == null);
        });
        test("daily overlapping or out-of-window slots are rejected instead of silently merged", () =>
        {
            var plan = Plan(); plan.Slots.Add(new() { Start = "11:00", End = "15:00" }); Throws<ArgumentException>(() => plan.Snapshot());
            plan = Plan(); plan.Slots[0].Start = "08:59"; Throws<ArgumentException>(() => plan.Snapshot());
            plan = Plan(); plan.Slots[0].End = "09:00"; Throws<ArgumentException>(() => plan.Snapshot());
            plan = Plan(); plan.End = plan.Start; Throws<ArgumentException>(() => plan.Snapshot());
        });
        test("daily time fields and executable paths reject ambiguous or shell command input", () =>
        {
            foreach (var value in new[] { "9:00", "24:00", "12:61", "tomorrow", "" }) Throws<ArgumentException>(() => DailyFocusPlan.Minute(value));
            var plan = Plan(); plan.ApplicationPaths["notepad"] = "notepad.exe --args"; Throws<ArgumentException>(() => plan.Snapshot());
            plan.ApplicationPaths["notepad"] = @"C:\Apps\other.exe"; Throws<ArgumentException>(() => plan.Snapshot());
            plan.Slots[0].Apps = "explorer.exe"; Throws<ArgumentException>(() => plan.Snapshot());
        });
        test("daily snapshot isolates app names and launch paths from later editor mutations", () =>
        {
            var source = Plan(); source.ApplicationPaths["notepad"] = @"C:\Apps\notepad.exe";
            var snapshot = source.Snapshot(); source.Slots[0].Apps = "chrome"; source.ApplicationPaths.Clear();
            Check(snapshot.Slots[0].AppNames().Contains("notepad") && snapshot.ApplicationPaths.ContainsKey("notepad"));
        });
        test("daily plan save atomically retains previous configuration and forced-exit marker", () =>
        {
            var directory = Path.Combine(root, "daily-plan"); var store = new DailyFocusPlanStore(directory);
            Check(!store.Load().Enabled);
            var plan = Plan(); store.Save(plan); var first = File.ReadAllBytes(Path.Combine(directory, "daily-focus.json"));
            plan.SuppressedWindow = plan.WindowAt(Time(9))!.Key; store.Save(plan);
            Check(store.Load().SuppressedWindow == plan.SuppressedWindow);
            Check(File.ReadAllBytes(Path.Combine(directory, "daily-focus.json.previous")).SequenceEqual(first));
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        });
        test("invalid save or future daily schema preserves existing files", () =>
        {
            var directory = Path.Combine(root, "daily-reject"); var store = new DailyFocusPlanStore(directory); store.Save(Plan());
            var path = Path.Combine(directory, "daily-focus.json"); var before = File.ReadAllBytes(path);
            var invalid = Plan(); invalid.Slots[0].Apps = "*"; Throws<ArgumentException>(() => store.Save(invalid));
            Check(File.ReadAllBytes(path).SequenceEqual(before));
            File.WriteAllText(path, "{\"Version\":2}"); before = File.ReadAllBytes(path);
            Throws<InvalidDataException>(() => store.Load()); Check(File.ReadAllBytes(path).SequenceEqual(before));
        });
        test("scheduled whitelist supports self-only gaps without weakening normal whitelist validation", () =>
        {
            var locked = FocusApplicationPolicy.CreateScheduled([]);
            Check(locked.IsScheduled && !locked.IsAllowed("notepad") && locked.IsAllowed("taskmgr") && locked.IsAllowed("app", true));
            var free = FocusApplicationPolicy.CreateScheduled([], true); Check(free.IsAllowed("notepad") && free.AllowAllApplications);
            Throws<ArgumentException>(() => FocusApplicationPolicy.Create(new FocusSettings { WhitelistEnabled = true, AllowedApps = [] }));
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Daily plan assertion failed."); }
    private static void Throws<T>(Action body) where T : Exception
    { try { body(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
