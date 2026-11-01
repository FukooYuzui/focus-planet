using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FocusGarden.App;
using FocusGarden.Core;
using Microsoft.Win32;

internal static partial class Program
{
    private sealed class DailyFixture : IDisposable
    {
        internal Fixture F = new(false);
        internal DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(8));
        internal List<FakeWorkspaceSession> Guards = [];
        internal int Bells;
        internal DailyFixture(bool all = false)
        {
            SetField(F.Window, "dailyHotkeyRegistered", true);
            SetField(F.Window, "dailyNow", new Func<DateTimeOffset>(() => Now));
            SetField(F.Window, "dailyAlarm", new Func<Task>(() => { Bells++; return Task.CompletedTask; }));
            var plan = new DailyFocusPlan { Enabled = true, Start = "09:00", End = "12:00", CountWholePeriod = all,
                Slots = [new() { Start = "09:00", End = "10:00", Label = "写作", Apps = "synthetic-editor" }, new() { Start = "10:00", End = "11:00", Label = "阅读", Apps = "synthetic-reader" }] };
            SetField(F.Window, "dailyPlan", plan);
            Field<DailyFocusEditor>(F.Window, "dailyEditor").Load(plan);
            SetWorkspaceFactory(F, (policy, _, _) => { var guard = new FakeWorkspaceSession { ScheduledPolicy = policy }; Guards.Add(guard); return Task.FromResult<IFocusWorkspaceSession>(guard); });
        }
        internal void Start() { Call(F.Window, "StartDailyFocusAsync"); F.Drain(); Assert(Active); }
        internal bool Active => Field<DailyFocusWindow?>(F.Window, "activeDailyWindow") != null;
        internal void Advance(double seconds)
        {
            Now = Now.AddSeconds(seconds);
            SetField(F.Window, "dailyTimestamp", Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * seconds));
            Call(F.Window, "DailyTick"); F.Drain();
        }
        internal string? StartTask(string title = "daily task", int minutes = 1)
        { var result = (string?)Call(F.Window, "StartDailyTaskAsync", title, minutes); F.Drain(); return result; }
        internal FocusSession TaskRow(string title = "daily task") => F.Store.GetSessions().Single(x => x.Task == title);
        public void Dispose() => F.Dispose();
    }
    private static void RunDailyCases()
    {
        Run("daily scheduler starts at the configured time and outside-window status is not falsely paused", () =>
        {
            using var d = new DailyFixture(); d.Now = d.Now.AddMinutes(-1);
            SetField(d.F.Window, "dailySchedulerReady", true); Call(d.F.Window, "DailyTick");
            Assert(!d.Active); Assert(!Field<DailyFocusEditor>(d.F.Window, "dailyEditor").RuntimeText.Text.Contains("今日已暂停"));
            d.Now = d.Now.AddMinutes(1); Call(d.F.Window, "DailyTick"); d.F.Drain(); Assert(d.Active); d.F.AssertHistory();
        });
        Run("daily launch authorization uses current wall time even before the next policy tick", () =>
        {
            using var d = new DailyFixture();
            var path = System.IO.Path.Combine(d.F.DirectoryPath, "synthetic-editor.exe");
            Field<DailyFocusPlan>(d.F.Window, "dailyPlan").ApplicationPaths["synthetic-editor"] = path;
            d.Start(); Assert((bool)Call(d.F.Window, "CanLaunchDailyApp", path)!);
            d.Now = d.Now.AddHours(1); Assert(!(bool)Call(d.F.Window, "CanLaunchDailyApp", path)!);
            d.Now = d.Now.AddHours(2); Assert(!(bool)Call(d.F.Window, "CanLaunchDailyApp", path)!); d.F.AssertHistory();
        });
        Run("daily bell is a non-silent two-second PCM wave without relying on system sound settings", () =>
        {
            var wave = DailyAlarm.CreateWave();
            Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
            Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
            Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4));
            Equal(22050, BitConverter.ToInt32(wave, 24)); Equal(22050 * 2 * 2, BitConverter.ToInt32(wave, 40));
            Assert(wave.Skip(44).Any(value => value != 0));
        });
        Run("daily idle protection creates no focus seconds and rejects normal timer overlap", () =>
        {
            using var d = new DailyFixture(); var before = d.F.AllRows(); d.Start(); d.Advance(2.1);
            Equal(before, d.F.AllRows()); Assert(d.F.Bridge.active);
            Assert(d.Guards.Single().ScheduledPolicy!.IsScheduled);
            Call(d.F.Window, "Begin", false); Call(d.F.Window, "StartBreak", d.F.Window, new RoutedEventArgs());
            Assert(Field<object?>(d.F.Window, "clock") == null); d.F.AssertHistory();
        });
        Run("daily task deadline rings once without disposing workspace or releasing website lease", () =>
        {
            using var d = new DailyFixture(); d.Start(); Equal<string?>(null, d.StartTask());
            for (int i = 0; i < 20; i++) d.Advance(3);
            Assert(d.Active && d.F.Bridge.active); Equal(0, d.Guards[0].DisposeCalls); Equal(1, d.Bells);
            Equal(60, d.TaskRow().ElapsedSeconds); Equal("completed", d.TaskRow().Status);
            d.Advance(3); d.Advance(3); Equal(1, d.Bells); Equal(60, d.TaskRow().ElapsedSeconds);
            Equal<string?>(null, d.StartTask("next task")); d.Advance(2.1);
            Equal(2, d.TaskRow("next task").ElapsedSeconds); d.F.AssertHistory();
        });
        Run("daily whole-period accounting never double credits a nested task clock", () =>
        {
            using var d = new DailyFixture(true); d.Start(); d.Advance(2); Equal<string?>(null, d.StartTask());
            d.Advance(2); Call(d.F.Window, "StopDailyTask", "emergency", "stopped"); d.Advance(2);
            Equal(2, d.F.Store.GetSessions().Count); Equal(6, d.TaskRow("全天专注").ElapsedSeconds); Equal(0, d.Bells);
            Assert(SendHotkey(d.F.Window, 9082)); Equal("emergency", d.TaskRow("全天专注").Status); d.F.AssertHistory();
        });
        Run("daily slot boundary replaces policy in place and unplanned time permits only self by default", () =>
        {
            using var d = new DailyFixture(); d.Start(); var guard = d.Guards[0];
            Assert(guard.ScheduledPolicy!.IsAllowed("synthetic-editor"));
            d.Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8)); d.Advance(0);
            Assert(!guard.ScheduledPolicy.IsAllowed("synthetic-editor") && guard.ScheduledPolicy.IsAllowed("synthetic-reader"));
            d.Now = d.Now.AddHours(1); d.Advance(0);
            Assert(!guard.ScheduledPolicy.IsAllowed("synthetic-reader") && !guard.ScheduledPolicy.AllowAllApplications);
            Equal(1, d.Guards.Count); Equal(0, guard.DisposeCalls); d.F.AssertHistory();
        });
        Run("daily forced hotkey preserves saved seconds and suppresses today's window across plan reopen", () =>
        {
            using var d = new DailyFixture(); d.Start(); d.StartTask(); d.Advance(2.1);
            Assert(SendHotkey(d.F.Window, 9081)); Assert(d.Active);
            Assert(SendHotkey(d.F.Window, 9082)); d.F.Drain();
            Assert(!d.Active && !d.F.Bridge.active); Equal(1, d.Guards[0].DisposeCalls);
            Equal(2, d.TaskRow().ElapsedSeconds); Equal("emergency", d.TaskRow().Status);
            var saved = new DailyFocusPlanStore(d.F.DirectoryPath).Load();
            Equal(saved.WindowAt(d.Now)!.Key, saved.SuppressedWindow);
            Call(d.F.Window, "StartDailyFocusAsync"); Assert(!d.Active);
            d.Now = d.Now.AddDays(1); d.Start(); Equal(2, d.Guards.Count); d.F.AssertHistory();
        });
        Run("daily schedule end releases all surfaces even while task timer has no active task", () =>
        {
            using var d = new DailyFixture(); d.Start(); d.Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(8)); d.Advance(0);
            Assert(!d.Active && !d.F.Bridge.active); Equal(1, d.Guards[0].DisposeCalls);
            Equal(0, Field<IReadOnlyList<WorkspaceBackdropWindow>>(d.F.Window, "workspaceBackdrops").Count); d.F.AssertHistory();
        });
        Run("task finishing exactly at daily end preserves its final seconds and rings once", () =>
        {
            using var d = new DailyFixture(); d.Now = new(2026, 11, 2, 11, 59, 0, TimeSpan.FromHours(8)); d.Start();
            Equal<string?>(null, d.StartTask());
            for (int i = 0; i < 20; i++) d.Advance(3);
            Assert(!d.Active); Equal(60, d.TaskRow().ElapsedSeconds); Equal("completed", d.TaskRow().Status); Equal(1, d.Bells);
            d.Advance(1); Equal(1, d.Bells); d.F.AssertHistory();
        });
        Run("daily lock releases current task without offline credits and unlock may resume scheduled protection", () =>
        {
            using var d = new DailyFixture(); d.Start(); d.StartTask(); d.Advance(2.1);
            Call(d.F.Window, "SessionChanged", d.F.Window, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)); Pump(); d.F.Drain();
            Assert(!d.Active); Equal(2, d.TaskRow().ElapsedSeconds); Equal("interrupted", d.TaskRow().Status);
            d.Now = d.Now.AddMinutes(10);
            Call(d.F.Window, "SessionChanged", d.F.Window, new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock)); Pump(); d.Start();
            Assert(Field<DateTimeOffset?>(d.F.Window, "dailyTaskDeadline") == null); Equal(2, d.TaskRow().ElapsedSeconds); d.F.AssertHistory();
        });
        Run("daily force exit during guard preparation cancels and disposes a late session without rearming", () =>
        {
            using var d = new DailyFixture(); var pending = new TaskCompletionSource<IFocusWorkspaceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetWorkspaceFactory(d.F, (_, _, _) => pending.Task);
            var start = (Task)Invoke(d.F.Window, "StartDailyFocusAsync")!; Assert(d.Active);
            SendHotkey(d.F.Window, 9082);
            var guard = new FakeWorkspaceSession(); pending.SetResult(guard); Wait(start); d.F.Drain();
            Assert(!d.Active); Equal(1, guard.DisposeCalls); Assert(Field<IFocusWorkspaceSession?>(d.F.Window, "workspaceSession") == null); d.F.AssertHistory();
        });
        Run("daily unavailable force-exit key prevents start and active plans cannot be edited", () =>
        {
            using var d = new DailyFixture(); SetField(d.F.Window, "dailyHotkeyRegistered", false);
            Call(d.F.Window, "StartDailyFocusAsync"); Assert(!d.Active && d.Guards.Count == 0);
            SetField(d.F.Window, "dailyHotkeyRegistered", true); d.Start();
            Assert(Call(d.F.Window, "SaveDailyPlanAsync", new DailyFocusPlan()) is string); Assert(d.Active);
            d.F.Window.Close(); Pump(); Assert(d.Active); d.F.AssertHistory();
        });
        Run("daily task validation and delayed callbacks do not fabricate focus time", () =>
        {
            using var d = new DailyFixture(); d.Start();
            Assert(d.StartTask("", 1) != null && d.StartTask("too long", 721) != null && d.StartTask("past end", 181) != null);
            Equal(1, d.F.Store.GetSessions().Count);
            d.StartTask(); d.Advance(10); Equal(0, d.TaskRow().ElapsedSeconds);
            d.Advance(2.1); Equal(2, d.TaskRow().ElapsedSeconds); d.F.AssertHistory();
        });
        Run("daily background contains editable task timer and current schedule hint without native windows", () =>
        {
            using var d = new DailyFixture(); d.Start(); Call(d.F.Window, "DailyTick");
            var backdrop = Field<IReadOnlyList<WorkspaceBackdropWindow>>(d.F.Window, "workspaceBackdrops").Single();
            Assert(!backdrop.IsVisible && backdrop.Owner == null);
            var panel = Field<DailyTaskPanel>(backdrop, "dailyTaskPanel"); Assert(panel.TaskInput.IsEnabled);
            Render(backdrop, "daily-focus-task-clock.png", 1440, 900);
            Call(d.F.Window, "Page", "daily"); Render(d.F.Window, "daily-focus-plan.png", 1200, 1000);
            d.StartTask(); Call(d.F.Window, "DailyTick"); Assert(!panel.TaskInput.IsEnabled); Assert(panel.StateText.Text.Contains("daily task"));
            d.F.AssertHistory();
        });
    }
}
