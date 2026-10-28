using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using FocusGarden.App;
using FocusGarden.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "FocusPlanet-StrictFocus-" + Guid.NewGuid().ToString("N"));
    private static int passed, failed;
    private static string? renderDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        if (args.Length == 2 && args[0] == "--render") renderDirectory = Path.GetFullPath(args[1]);
        var app = new TestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Match Application.Run's UI continuation context while keeping every window hidden.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        if (args.Length == 2 && args[0] == "--soak")
        {
            if (!int.TryParse(args[1], out var minutes) || minutes is < 1 or > 60) throw new ArgumentException("Soak duration must be 1-60 minutes.");
            try { Run("real-time synthetic continuous operation", () => Soak(minutes)); }
            finally { app.Shutdown(); }
            return failed == 0 ? 0 : 1;
        }
        // No top-level window is shown, global input is not sent, and system events are simulated in-process.
        try
        {
            Run("legacy optional settings load into an unlocked UI and retain exact historical rows", () =>
            {
                using var f = new Fixture(false, legacyJson: true);
                Assert(!Field<FocusSettings>(f.Window, "settings").StrictFocus);
                Assert(Field<CheckBox>(f.Window, "StrictFocusCheck").IsChecked == false);
                Assert(Field<FrameworkElement>(f.Window, "SetupPanel").IsEnabled);
                f.AssertHistory();
            });

            Run("cancelled strict confirmation creates no session, clock, or active restrictions", () =>
            {
                using var f = new Fixture(); int calls = 0;
                f.Confirm(seconds => { calls++; Equal(60, seconds); return false; });
                var before = f.AllRows();
                Call(f.Window, "Begin", false);
                Equal(1, calls); Equal(before, f.AllRows());
                Assert(Field<object?>(f.Window, "clock") is null);
                Assert(Field<long?>(f.Window, "sessionId") is null);
                Assert(!Field<bool>(f.Window, "activeStrictFocus"));
                Assert(!Field<bool>(f.Window, "startingFocus"));
                Assert(!f.Bridge.active);
                Assert(Field<FrameworkElement>(f.Window, "SetupPanel").IsEnabled);
            });

            Run("confirmed strict focus starts once and disables user exit controls", () =>
            {
                using var f = new Fixture(); int calls = 0;
                f.Confirm(seconds => { calls++; Equal(60, seconds); return true; });
                Call(f.Window, "Page", "focus");
                Render(f.Window, "严格专注-开始前.png");
                f.Start();
                Equal(1, calls); Equal(2, f.Store.GetSessions().Count);
                Assert(Field<bool>(f.Window, "activeStrictFocus"));
                Assert(!Field<FrameworkElement>(f.Window, "SetupPanel").IsEnabled);
                Assert(!Field<Button>(f.Window, "EmergencyButton").IsEnabled);
                Assert(Field<Button>(f.Window, "EmergencyButton").Content.ToString()!.Contains("禁用"));
                Assert(Field<TextBlock>(f.Window, "HotkeyHint").Text.Contains("严格专注"));
                Assert(f.Bridge.active); f.AssertHistory();
                Render(f.Window, "严格专注-计时中.png");
            });

            Run("strict main button and direct request cannot change saved progress or end focus", () =>
            {
                using var f = new Fixture(); f.Start(); f.Tick(2.1);
                var before = f.AllRows(); var id = Field<long?>(f.Window, "sessionId");
                Call(f.Window, "EmergencyClick", f.Window, new RoutedEventArgs());
                Call(f.Window, "ConfirmEmergency");
                Equal(before, f.AllRows()); Equal(id, Field<long?>(f.Window, "sessionId"));
                Assert(f.Bridge.active); Equal("running", f.Current.Status);
                Assert(Field<TextBlock>(f.Window, "StatusText").Text.Contains("严格专注"));
            });

            Run("strict WM_HOTKEY is handled but cannot bypass the session exit guard", () =>
            {
                using var f = new Fixture(); f.Start(); f.Tick(2.1); var before = f.AllRows();
                Assert(SendHotkey(f.Window, 9081));
                Equal(before, f.AllRows()); Equal("running", f.Current.Status); Assert(f.Bridge.active);
                Assert(!SendHotkey(f.Window, 9999));
            });

            Run("strict main window Close is cancelled without opening a confirmation dialog", () =>
            {
                using var f = new Fixture(); f.Start(); f.Tick(1.1);
                bool closed = false; f.Window.Closed += (_, _) => closed = true;
                var before = f.AllRows(); f.Window.Close();
                Assert(!closed); Equal(before, f.AllRows()); Assert(f.Bridge.active);
                Assert(Field<object?>(f.Window, "clock") is not null);
            });

            Run("current strict lock is a snapshot independent of mutable UI and stored preferences", () =>
            {
                using var f = new Fixture(); f.Start();
                Field<CheckBox>(f.Window, "StrictFocusCheck").IsChecked = false;
                var settings = Field<FocusSettings>(f.Window, "settings"); settings.StrictFocus = false;
                f.Store.SaveSettings(settings);
                Assert(SendHotkey(f.Window, 9081)); Call(f.Window, "ConfirmEmergency");
                Assert(Field<bool>(f.Window, "activeStrictFocus")); Equal("running", f.Current.Status);
                Assert(f.Bridge.active); f.AssertHistory();
            });

            Run("confirmation cannot be reentered by another start or break request", () =>
            {
                using var f = new Fixture(); int calls = 0;
                f.Confirm(_ =>
                {
                    calls++;
                    Call(f.Window, "Begin", false);
                    Call(f.Window, "StartBreak", f.Window, new RoutedEventArgs());
                    Assert(Field<object?>(f.Window, "clock") is null);
                    Equal(1, f.Store.GetSessions().Count);
                    return true;
                });
                f.Start(); Equal(1, calls); Equal(2, f.Store.GetSessions().Count);
                Assert(!Field<bool>(f.Window, "startingFocus"));
                Assert(!Field<bool>(f.Window, "breaking")); Assert(Field<bool>(f.Window, "activeStrictFocus"));
            });

            Run("confirmation locks the mode that was presented even when its delegate changes settings", () =>
            {
                using var f = new Fixture();
                f.Confirm(_ =>
                {
                    Field<CheckBox>(f.Window, "StrictFocusCheck").IsChecked = false;
                    Field<FocusSettings>(f.Window, "settings").StrictFocus = false;
                    return true;
                });
                f.Start(); Assert(Field<bool>(f.Window, "activeStrictFocus"));
                Assert(SendHotkey(f.Window, 9081)); Equal("running", f.Current.Status);
            });

            Run("strict countdown completes through Tick with exact seconds and restores controls", () =>
            {
                using var f = new Fixture(); f.Start();
                for (var i = 0; i < 25 && Field<object?>(f.Window, "clock") is not null; i++) f.Tick(3.05);
                Assert(Field<object?>(f.Window, "clock") is null);
                Equal("completed", f.Current.Status); Equal(60, f.Current.ElapsedSeconds);
                Equal(60L, f.CurrentDaySeconds());
                Assert(!Field<bool>(f.Window, "activeStrictFocus")); Assert(!f.Bridge.active);
                Assert(Field<FrameworkElement>(f.Window, "SetupPanel").IsEnabled);
                Assert(Field<Button>(f.Window, "EmergencyButton").IsEnabled);
                Equal(Visibility.Visible, Field<Button>(f.Window, "StartButton").Visibility);
                var before = f.AllRows(); f.Tick(1.1); Equal(before, f.AllRows()); f.AssertHistory();
            });

            Run("every new strict session asks again and cancellation leaves finished history intact", () =>
            {
                using var f = new Fixture(); int calls = 0;
                f.Confirm(_ => ++calls == 1); f.Start();
                for (var i = 0; i < 25 && Field<object?>(f.Window, "clock") is not null; i++) f.Tick(3.05);
                var before = f.AllRows(); Call(f.Window, "Begin", false);
                Equal(2, calls); Equal(before, f.AllRows()); Assert(Field<object?>(f.Window, "clock") is null);
            });

            Run("ordinary focus still exits through its global-hotkey handler without strict confirmation", () =>
            {
                using var f = new Fixture(false); f.Confirm(_ => throw new Exception("Ordinary focus asked for strict confirmation."));
                f.Start(); f.Tick(2.1); var before = f.Current.ElapsedSeconds;
                Assert(SendHotkey(f.Window, 9081));
                Equal("emergency", f.Current.Status); Equal(before, f.Current.ElapsedSeconds);
                Assert(!f.Bridge.active); Assert(Field<object?>(f.Window, "clock") is null); f.AssertHistory();
            });

            Run("a break remains immediately endable when strict preference is enabled", () =>
            {
                using var f = new Fixture(); f.Confirm(_ => throw new Exception("Break asked for strict confirmation."));
                var before = f.AllRows(); Call(f.Window, "StartBreak", f.Window, new RoutedEventArgs());
                Assert(Field<bool>(f.Window, "breaking")); Assert(!Field<bool>(f.Window, "activeStrictFocus"));
                Assert(Field<Button>(f.Window, "EmergencyButton").IsEnabled); Assert(!f.Bridge.active);
                Call(f.Window, "ConfirmEmergency");
                Assert(Field<object?>(f.Window, "clock") is null); Equal(before, f.AllRows());
            });

            Run("error recovery releases strict restrictions and retains every recorded second", () =>
            {
                using var f = new Fixture(); f.Start(); f.Tick(2.1);
                var seconds = f.Current.ElapsedSeconds; var days = f.CurrentDaySeconds();
                f.Window.ReleaseForError();
                f.AssertReleased("interrupted", seconds, days); f.AssertHistory();
            });

            foreach (var strict in new[] { false, true })
                Run((strict ? "strict" : "ordinary") + " focus survives UI gaps past one and two minutes without leaving immersion or crediting gaps", () =>
                {
                    using var f = new Fixture(strict);
                    Field<TextBox>(f.Window, "FocusMinutesInput").Text = "3";
                    f.Start();
                    var session = Field<long?>(f.Window, "sessionId");
                    var timer = Field<object>(f.Window, "clock");
                    var immersive = CreateImmersion(strict, () => throw new Exception("Unexpected emergency callback during gap recovery."));
                    SetField(f.Window, "immersion", immersive);
                    bool closed = false; immersive.Closed += (_, _) => closed = true;

                    void AssertStillRunning()
                    {
                        Equal("running", f.Current.Status);
                        Equal(session, Field<long?>(f.Window, "sessionId"));
                        Assert(ReferenceEquals(timer, Field<object>(f.Window, "clock")));
                        Assert(ReferenceEquals(immersive, Field<object>(f.Window, "immersion")));
                        Assert(!closed && f.Bridge.active);
                        Equal(strict, Field<bool>(f.Window, "activeStrictFocus"));
                        Assert(!Field<FrameworkElement>(f.Window, "SetupPanel").IsEnabled);
                        Equal(!strict, Field<Button>(f.Window, "EmergencyButton").IsEnabled);
                        f.AssertHistory();
                    }
                    void SkipGap(double seconds)
                    {
                        var rows = f.AllRows(); var elapsed = f.Current.ElapsedSeconds; var daySeconds = f.CurrentDaySeconds();
                        f.Tick(seconds);
                        Equal(rows, f.AllRows()); Equal(elapsed, f.Current.ElapsedSeconds); Equal(daySeconds, f.CurrentDaySeconds());
                        AssertStillRunning();
                    }

                    for (var i = 0; i < 30 && f.Current.ElapsedSeconds < 60; i++) f.Tick(3.05);
                    Assert(f.Current.ElapsedSeconds >= 60 && f.Current.ElapsedSeconds < 65);
                    SkipGap(6.1);
                    var afterFirstGap = f.Current.ElapsedSeconds;
                    f.Tick(1.1); Assert(f.Current.ElapsedSeconds > afterFirstGap); AssertStillRunning();

                    for (var i = 0; i < 30 && f.Current.ElapsedSeconds < 120; i++) f.Tick(3.05);
                    Assert(f.Current.ElapsedSeconds >= 120 && f.Current.ElapsedSeconds < 125);
                    SkipGap(12);
                    SkipGap(7200);
                    for (var i = 0; i < 30 && Field<object?>(f.Window, "clock") is not null; i++) f.Tick(3.05);

                    f.AssertReleased("completed", 180, 180);
                    Assert(closed); Assert(Field<object?>(f.Window, "immersion") is null);
                    Equal(2, f.Store.GetSessions().Count);
                    var completedRows = f.AllRows(); f.Tick(1.1); Equal(completedRows, f.AllRows());
                    f.AssertHistory();
                });

            Run("a real growth database writer lock cannot end minute-one focus and later growth settles once", () =>
            {
                using var f = new Fixture();
                Field<TextBox>(f.Window, "FocusMinutesInput").Text = "3";
                f.Start();
                var session = Field<long?>(f.Window, "sessionId");
                var timer = Field<object>(f.Window, "clock");
                var immersive = CreateImmersion(true, () => throw new Exception("Growth contention requested an emergency exit."));
                SetField(f.Window, "immersion", immersive);
                bool closed = false; immersive.Closed += (_, _) => closed = true;
                for (var i = 0; i < 80 && f.Current.ElapsedSeconds < 58; i++) f.Tick(1.01);
                Assert(f.Current.ElapsedSeconds >= 58 && f.Current.ElapsedSeconds < 60);
                Assert((bool)Call(f.Window, "RefreshPlanet")!);
                var beforeGrowth = Field<PlanetSnapshot>(f.Window, "planetSnapshot").TotalGrowth;
                Equal(0L, beforeGrowth);

                using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(f.DirectoryPath, "planet.db"), Pooling = false, DefaultTimeout = 1
                }.ToString());
                blocker.Open();
                using var command = blocker.CreateCommand(); command.CommandText = "BEGIN IMMEDIATE"; command.ExecuteNonQuery();
                var watch = new Stopwatch();
                try
                {
                    SetField(f.Window, "planetTick", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 6);
                    watch.Start(); f.Tick(3.05); watch.Stop();
                    Assert(watch.Elapsed < TimeSpan.FromSeconds(5));
                    Equal("running", f.Current.Status); Equal(session, Field<long?>(f.Window, "sessionId"));
                    Assert(ReferenceEquals(timer, Field<object>(f.Window, "clock")));
                    Assert(ReferenceEquals(immersive, Field<object>(f.Window, "immersion")));
                    Assert(!closed && f.Bridge.active && Field<bool>(f.Window, "activeStrictFocus"));
                    Assert(f.Current.ElapsedSeconds >= 60 && f.Current.ElapsedSeconds < 65);
                    Equal((long)f.Current.ElapsedSeconds, f.CurrentDaySeconds());
                    Equal(beforeGrowth, Field<PlanetSnapshot>(f.Window, "planetSnapshot").TotalGrowth);
                    f.AssertHistory();
                }
                finally { command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); }

                var focusRows = f.AllRows();
                Assert((bool)Call(f.Window, "RefreshPlanet")!);
                Equal(1L, Field<PlanetSnapshot>(f.Window, "planetSnapshot").TotalGrowth);
                for (var i = 0; i < 3; i++) Assert((bool)Call(f.Window, "RefreshPlanet")!);
                Equal(1L, Field<PlanetSnapshot>(f.Window, "planetSnapshot").TotalGrowth);
                Equal(focusRows, f.AllRows());
                var beforeResume = f.Current.ElapsedSeconds; f.Tick(1.1);
                Assert(f.Current.ElapsedSeconds > beforeResume);
                Equal("running", f.Current.Status); Assert(!closed && f.Bridge.active);
                Assert(Field<bool>(f.Window, "activeStrictFocus")); f.AssertHistory();
                Console.WriteLine($"Growth writer contention returned after {watch.Elapsed.TotalMilliseconds:0} ms; historical focus rows unchanged.");
            });

            Run("simulated suspend releases strict focus while keeping saved progress", () =>
            {
                using var f = new Fixture(); f.Start(); f.Tick(2.1);
                var seconds = f.Current.ElapsedSeconds; var days = f.CurrentDaySeconds();
                Call(f.Window, "PowerChanged", f.Window, new PowerModeChangedEventArgs(PowerModes.Suspend)); Pump();
                f.AssertReleased("interrupted", seconds, days); f.AssertHistory();
            });

            foreach (var reason in new[] { SessionSwitchReason.SessionLock, SessionSwitchReason.SessionLogoff })
                Run("simulated " + reason + " releases strict focus and preserves per-day seconds", () =>
                {
                    using var f = new Fixture(); f.Start(); f.Tick(2.1);
                    var seconds = f.Current.ElapsedSeconds; var days = f.CurrentDaySeconds();
                    Call(f.Window, "SessionChanged", f.Window, new SessionSwitchEventArgs(reason)); Pump();
                    f.AssertReleased("interrupted", seconds, days); f.AssertHistory();
                });

            Run("strict immersion blocks click, Esc and Close yet its release closes normally", () =>
            {
                using var f = new Fixture(); f.Start(); f.Tick(1.1);
                int callbacks = 0;
                var immersive = CreateImmersion(true, () => { callbacks++; Call(f.Window, "ConfirmEmergency"); });
                SetField(f.Window, "immersion", immersive);
                var button = Find<Button>(immersive, "EmergencyExitButton");
                Assert(!button.IsEnabled); Assert(Find<TextBlock>(immersive, "ExitHint").Text.Contains("严格专注"));
                var before = f.AllRows(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var key = Escape(immersive); Assert(key.Handled);
                bool closed = false; immersive.Closed += (_, _) => closed = true;
                immersive.Close(); Pump();
                Assert(!closed); Equal(0, callbacks); Equal(before, f.AllRows());
                Call(immersive, "UpdateTime", "00:59"); Render(immersive, "严格专注-全屏.png", 1440, 900);
                f.Window.ReleaseForError();
                Assert(closed); Assert(Field<object?>(f.Window, "immersion") is null); Assert(!f.Bridge.active);
            });

            Run("ordinary immersion preserves click, Esc and close-request callbacks", () =>
            {
                int callbacks = 0; var immersive = CreateImmersion(false, () => callbacks++);
                try
                {
                    Assert(Find<Button>(immersive, "EmergencyExitButton").IsEnabled);
                    Find<Button>(immersive, "EmergencyExitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Equal(1, callbacks); Assert(Escape(immersive).Handled); Equal(2, callbacks);
                    immersive.Close(); Pump(); Equal(3, callbacks);
                }
                finally { Call(immersive, "Release"); }
            });

            Run("a focus database writer lock leaves Tick and dispatcher responsive, then saves each interval once", () =>
            {
                using var f = new Fixture(); f.Start();
                var before = f.AllRows();
                using var blocker = OpenWriterLock(Path.Combine(f.DirectoryPath, "focus.db"));
                var watch = Stopwatch.StartNew();
                try
                {
                    f.Tick(2.1, waitForFocus: false); watch.Stop();
                    Assert(watch.Elapsed < TimeSpan.FromMilliseconds(500));
                    Assert(!Field<Task>(f.Window, "progressTask").IsCompleted);
                    var heartbeat = false;
                    f.Window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => heartbeat = true));
                    Wait(Task.Delay(120));
                    Assert(heartbeat);
                    Equal(before, f.AllRows()); // WAL reads see only committed seconds while the writer waits.
                    f.Tick(1.1, waitForFocus: false);
                    Assert(Field<object?>(f.Window, "clock") is not null && f.Bridge.active);
                }
                finally { Rollback(blocker); }
                f.Drain();
                Equal(3, f.Current.ElapsedSeconds); Equal(3L, f.CurrentDaySeconds());
                Equal("running", f.Current.Status);
                var saved = f.AllRows(); f.Drain(); Equal(saved, f.AllRows());
                f.Window.ReleaseForError(); f.AssertReleased("interrupted", 3, 3); f.AssertHistory();
                Console.WriteLine($"Focus writer contention: Tick returned in {watch.Elapsed.TotalMilliseconds:0.###} ms; dispatcher heartbeat ran before unlock.");
            });

            foreach (var duringSessionInsert in new[] { false, true })
                Run("lock then unlock during asynchronous " + (duringSessionInsert ? "session insertion" : "settings save") + " cancels startup without credit", () =>
                {
                    using var f = new Fixture();
                    var queue = Field<SerialWorkQueue>(f.Window, "focusWork");
                    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                    Task? blocking = null;
                    void BlockQueue() => blocking = queue.RunAsync(() => { entered.Set(); release.Wait(); });
                    if (duringSessionInsert) f.Confirm(_ => { BlockQueue(); return true; });
                    else BlockQueue();
                    var begin = (Task)Invoke(f.Window, "Begin", false)!;
                    try
                    {
                        WaitUntil(() => entered.IsSet);
                        Assert(!begin.IsCompleted && Field<bool>(f.Window, "startingFocus"));
                        Call(f.Window, "SessionChanged", f.Window, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)); Pump();
                        Call(f.Window, "SessionChanged", f.Window, new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock)); Pump();
                        Assert(!Field<bool>(f.Window, "desktopLocked"));
                    }
                    finally { release.Set(); }
                    Wait(blocking!); Wait(begin); f.Drain();
                    Assert(Field<object?>(f.Window, "clock") is null && Field<long?>(f.Window, "sessionId") is null);
                    Assert(!Field<bool>(f.Window, "startingFocus") && !f.Bridge.active);
                    var added = f.Store.GetSessions().Where(row => row.Task == "strict test").ToArray();
                    Assert(added.Length <= 1);
                    foreach (var row in added) { Equal(0, row.ElapsedSeconds); Equal("interrupted", row.Status); }
                    f.AssertHistory();
                });

            Run("a synchronously failed close disables new focus and retries the real close after the queue drains", () =>
            {
                using var f = new Fixture(); var history = f.AllRows();
                var queue = Field<SerialWorkQueue>(f.Window, "focusWork");
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var blocking = queue.RunAsync(() => { entered.Set(); release.Wait(); });
                WaitUntil(() => entered.IsSet);
                var accepted = Enumerable.Range(0, 256).Select(_ => queue.RunAsync(() => { })).ToArray();
                bool closed = false; f.Window.Closed += (_, _) => closed = true;
                try
                {
                    f.Window.Close(); Pump();
                    Assert(!closed && Field<bool>(f.Window, "shuttingDown"));
                    Assert(!Field<Button>(f.Window, "StartButton").IsEnabled);
                    Assert(!Field<Button>(f.Window, "BreakButton").IsEnabled);
                    Assert(!Field<DispatcherTimer>(f.Window, "timer").IsEnabled);
                    Call(f.Window, "Begin", false);
                    Assert(Field<object?>(f.Window, "clock") is null && !f.Bridge.active);
                    Equal(history, f.AllRows());
                }
                finally { release.Set(); }
                Wait(Task.WhenAll(accepted.Append(blocking)));
                f.Window.Close(); WaitUntil(() => closed);
                Assert(Field<bool>(f.Window, "allowClose")); Equal(history, f.AllRows());
            });

            Run("focus heatmap retains each cell and hidden pages defer updates until shown", () =>
            {
                using var f = new Fixture(false);
                Call(f.Window, "Page", "focus"); f.Drain();
                var cells = new Dictionary<string, Border>(Field<Dictionary<string, Border>>(f.Window, "heatmapCells"));
                Equal(DateTime.IsLeapYear(DateTime.Today.Year) ? 366 : 365, cells.Count);
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                f.Start(); f.Tick(2.1); Call(f.Window, "RefreshStatistics"); f.Drain();
                Equal(2, Field<Dictionary<string, int>>(f.Window, "heatmapValues")[today]);
                foreach (var pair in cells) Assert(ReferenceEquals(pair.Value, Field<Dictionary<string, Border>>(f.Window, "heatmapCells")[pair.Key]));
                var historyVisuals = Field<StackPanel>(f.Window, "HistoryPanel").Children.Cast<UIElement>().ToArray();
                Call(f.Window, "Page", "planet");
                f.Tick(2.1); Call(f.Window, "RefreshStatistics"); f.Drain();
                Equal(2, Field<Dictionary<string, int>>(f.Window, "heatmapValues")[today]);
                Assert(historyVisuals.SequenceEqual(Field<StackPanel>(f.Window, "HistoryPanel").Children.Cast<UIElement>()));
                Call(f.Window, "Page", "focus"); f.Drain();
                Equal(4, Field<Dictionary<string, int>>(f.Window, "heatmapValues")[today]);
                foreach (var pair in cells) Assert(ReferenceEquals(pair.Value, Field<Dictionary<string, Border>>(f.Window, "heatmapCells")[pair.Key]));
                f.AssertHistory();
            });

            Run("dashboard reuses application rows and unchanged token geometry, and defers hidden rendering", () =>
            {
                using var f = new Fixture();
                var queue = Field<SerialWorkQueue>(f.Window, "focusWork"); var usage = Field<AppUsageStore>(f.Window, "usageStore");
                Wait(queue.RunAsync(() => usage.AddCredits([new("alpha.exe", DateTime.Today, 5), new("beta.exe", DateTime.Today, 3)])));
                Call(f.Window, "Page", "focus"); f.Drain();
                var panels = UsagePanels(f.Window);
                Equal(2, panels.Count);
                var calendar = Field<object>(f.Window, "tokenCalendar"); var builds = Property<int>(calendar, "SceneBuildCount");
                Call(f.Window, "RefreshTokenUi"); Call(f.Window, "RefreshTokenUi"); f.Drain();
                Equal(builds, Property<int>(calendar, "SceneBuildCount"));
                Wait(queue.RunAsync(() => usage.AddCredits([new("beta.exe", DateTime.Today, 4)])));
                Call(f.Window, "RefreshUsageUi"); f.Drain();
                var refreshed = UsagePanels(f.Window);
                foreach (var pair in panels) Assert(ReferenceEquals(pair.Value, refreshed[pair.Key]));
                Assert(ReferenceEquals(panels["beta.exe"], Field<StackPanel>(f.Window, "UsageRows").Children[0]));
                Equal("7 秒", ((TextBlock)((DockPanel)panels["beta.exe"].Children[0]).Children[0]).Text);
                Call(f.Window, "Page", "planet");
                Wait(queue.RunAsync(() => usage.AddCredits([new("alpha.exe", DateTime.Today, 5)])));
                SetField(f.Window, "cachedTokenDaily", new Dictionary<string, long> { [DateTime.Today.ToString("yyyy-MM-dd")] = 1234 });
                Call(f.Window, "RefreshUsageUi"); Call(f.Window, "RefreshTokenUi"); f.Drain();
                Equal("5 秒", ((TextBlock)((DockPanel)panels["alpha.exe"].Children[0]).Children[0]).Text);
                Equal(builds, Property<int>(calendar, "SceneBuildCount"));
                Call(f.Window, "Page", "focus"); f.Drain();
                Equal("10 秒", ((TextBlock)((DockPanel)panels["alpha.exe"].Children[0]).Children[0]).Text);
                Equal(builds + 1, Property<int>(calendar, "SceneBuildCount"));
                f.AssertHistory();
            });

            Run("CSV replaces an existing file safely, escapes formulas, and completes before close disposes storage", () =>
            {
                using var f = new Fixture();
                var queue = Field<SerialWorkQueue>(f.Window, "focusWork");
                Wait(queue.RunAsync(() =>
                {
                    var now = DateTimeOffset.Now;
                    var id = f.Store.BeginSession("=SUM(1,2) \"quoted\"", 1, now);
                    f.Store.AddProgress(id, 1, now.AddSeconds(1)); f.Store.EndSession(id, "completed", now.AddSeconds(1));
                }));
                var path = Path.Combine(f.DirectoryPath, "export.csv"); File.WriteAllText(path, "existing export");
                var snapshot = f.AllRows();
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var blocking = queue.RunAsync(() => { entered.Set(); release.Wait(); }); WaitUntil(() => entered.IsSet);
                var export = (Task)Invoke(f.Window, "ExportRecordsAsync", path)!; SetField(f.Window, "exportTask", export);
                var closing = (Task)Invoke(f.Window, "PrepareCloseAsync")!;
                try
                {
                    Pump(); Assert(!export.IsCompleted && !closing.IsCompleted);
                    Equal("existing export", File.ReadAllText(path));
                }
                finally { release.Set(); }
                Wait(blocking); Wait(closing);
                Assert(export.IsCompletedSuccessfully && Field<bool>(f.Window, "allowClose"));
                var csv = File.ReadAllText(path);
                Assert(csv.StartsWith("编号,任务,开始时间", StringComparison.Ordinal));
                Assert(csv.Contains("\"'=SUM(1,2) \"\"quoted\"\"\"", StringComparison.Ordinal));
                Assert(!csv.Contains("existing export", StringComparison.Ordinal));
                Equal(3, File.ReadAllLines(path).Length);
                Equal(0, Directory.GetFiles(f.DirectoryPath, ".focus-export-*.tmp").Length);
                Equal(snapshot, f.AllRows()); f.AssertHistory();
            });
            RunWorkspaceCases();
        }
        finally { app.Shutdown(); }
        Console.WriteLine($"Strict focus: {passed} passed, {failed} failed; synthetic fixtures: {Root}");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception); }
    }

    private static void RunWorkspaceCases()
    {
        Run("whitelist is disabled for legacy settings and ordinary focus never creates a workspace guard", () =>
        {
            using var f = new Fixture(false, legacyJson: true);
            var settings = Field<FocusSettings>(f.Window, "settings");
            Assert(!settings.WhitelistEnabled && settings.AllowedApps.Length == 0);
            SetWorkspaceFactory(f, (_, _, _) => throw new Exception("Disabled whitelist invoked its native guard."));
            f.Start(); f.Tick(1.1); Assert(SendHotkey(f.Window, 9081));
            f.AssertReleased("emergency", 1, 1); f.AssertHistory();
        });

        Run("whitelist start captures multiple applications and suppresses immersive fullscreen for this session", () =>
        {
            using var f = new Fixture();
            ConfigureWhitelist(f);
            Field<CheckBox>(f.Window, "ImmersiveCheck").IsChecked = true;
            var guard = new FakeWorkspaceSession(); FocusApplicationPolicy? policy = null; int starts = 0;
            SetWorkspaceFactory(f, (snapshot, _, _) => { starts++; policy = snapshot; return Task.FromResult<IFocusWorkspaceSession>(guard); });
            Call(f.Window, "Page", "rules");
            var running = Field<ComboBox>(f.Window, "RunningAllowedApps");
            running.ItemsSource = new[] { "synthetic-editor.exe", "synthetic-reader.exe" }; running.SelectedIndex = 0;
            Render(f.Window, "whitelist-settings-960.png", 960, 900);
            Call(f.Window, "Page", "focus"); f.Drain();
            Render(f.Window, "whitelist-focus-ready-960.png", 960, 900);
            f.Start();
            Equal(1, starts); Assert(policy is not null && policy.WhitelistEnabled && policy.HideTaskbar && policy.BlockDesktopShortcuts);
            Assert(policy!.AllowedApps.SequenceEqual(new[] { "synthetic-editor", "synthetic-reader" }));
            Assert(ReferenceEquals(guard, Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession")));
            var dock = Field<WorkspaceDockWindow>(f.Window, "workspaceDock");
            Assert(dock.Owner is null && !dock.IsVisible);
            Assert(!f.Window.OwnedWindows.OfType<Window>().Contains(dock));
            Render(f.Window, "whitelist-focus-active-960.png", 960, 900);
            Assert(Field<object?>(f.Window, "immersion") is null);
            Equal(Visibility.Collapsed, Field<Button>(f.Window, "FullscreenButton").Visibility);
            Assert(!Field<CheckBox>(f.Window, "WhitelistEnabledCheck").IsEnabled);
            Assert(!Field<TextBox>(f.Window, "AllowedAppsInput").IsEnabled);
            Call(f.Window, "EnterFullscreen");
            Assert(Field<object?>(f.Window, "immersion") is null);
            var settings = Field<FocusSettings>(f.Window, "settings");
            settings.AllowedApps[0] = "changed"; settings.WhitelistEnabled = false; settings.HideTaskbar = false;
            Field<TextBox>(f.Window, "AllowedAppsInput").Text = "changed.exe";
            Field<CheckBox>(f.Window, "WhitelistEnabledCheck").IsChecked = false;
            f.Tick(1.1);
            Assert(guard.PulseCalls > 0 && policy.IsAllowed("synthetic-editor.exe") && !policy.IsAllowed("changed.exe"));
            Assert(SendHotkey(f.Window, 9081)); Assert(Field<object?>(f.Window, "clock") is not null);
            Equal(0, guard.DisposeCalls);
            for (var i = 0; i < 25 && Field<object?>(f.Window, "clock") is not null; i++) f.Tick(3.05);
            f.AssertReleased("completed", 60, 60); Equal(1, guard.DisposeCalls);
            Assert(Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession") is null);
            f.AssertHistory();
        });

        Run("whitelist preparation failure retains a zero-second interrupted record and never enables restrictions", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            SetWorkspaceFactory(f, (_, _, _) => Task.FromException<IFocusWorkspaceSession>(new InvalidOperationException("synthetic no allowed window")));
            Call(f.Window, "Begin", false); f.Drain();
            f.AssertReleased("interrupted", 0, 0);
            Assert(Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession") is null);
            Assert(Field<object?>(f.Window, "immersion") is null);
            Assert(!Field<bool>(f.Window, "startingFocus")); f.AssertHistory();
        });

        Run("lock and unlock during asynchronous whitelist preparation cancel the returned guard before any focus starts", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            var prepared = new TaskCompletionSource<IFocusWorkspaceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guard = new FakeWorkspaceSession(); int starts = 0; CancellationToken preparationToken = default;
            SetWorkspaceFactory(f, (_, _, token) => { starts++; preparationToken = token; return prepared.Task; });
            var start = (Task)Invoke(f.Window, "Begin", false)!;
            WaitUntil(() => starts == 1);
            Assert(Field<object?>(f.Window, "clock") is null && !f.Bridge.active);
            Call(f.Window, "SessionChanged", f.Window, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)); Pump();
            Call(f.Window, "SessionChanged", f.Window, new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock)); Pump();
            Assert(preparationToken.IsCancellationRequested);
            prepared.SetResult(guard); Wait(start); f.Drain();
            f.AssertReleased("interrupted", 0, 0); Equal(1, guard.DisposeCalls);
            Assert(Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession") is null);
            Assert(!Field<bool>(f.Window, "startingFocus")); f.AssertHistory();
        });

        Run("error recovery cancels preparation even when the factory ignores cancellation and returns a late guard", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            var prepared = new TaskCompletionSource<IFocusWorkspaceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guard = new FakeWorkspaceSession(); int starts = 0; CancellationToken preparationToken = default;
            SetWorkspaceFactory(f, (_, _, token) => { starts++; preparationToken = token; return prepared.Task; });
            var start = (Task)Invoke(f.Window, "Begin", false)!;
            WaitUntil(() => starts == 1);
            var generation = Field<long>(f.Window, "sessionEventGeneration");
            f.Window.ReleaseForError();
            Assert(preparationToken.IsCancellationRequested);
            Equal(generation, Field<long>(f.Window, "sessionEventGeneration"));
            Assert(Field<object?>(f.Window, "clock") is null && !f.Bridge.active);
            prepared.SetResult(guard); Wait(start); f.Drain();
            f.AssertReleased("interrupted", 0, 0); Equal(1, guard.DisposeCalls);
            Assert(Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession") is null);
            Assert(Field<WorkspaceDockWindow?>(f.Window, "workspaceDock") is null);
            Assert(Field<CancellationTokenSource?>(f.Window, "workspaceStartCancellation") is null);
            Assert(!Field<bool>(f.Window, "startingFocus"));
            f.Window.ReleaseForError(); Equal(1, guard.DisposeCalls); f.AssertHistory();
        });

        Run("ordinary whitelist focus releases the guard through the existing emergency shortcut", () =>
        {
            using var f = new Fixture(false); ConfigureWhitelist(f);
            var guard = new FakeWorkspaceSession();
            SetWorkspaceFactory(f, (_, _, _) => Task.FromResult<IFocusWorkspaceSession>(guard));
            f.Start(); f.Tick(2.1); Assert(SendHotkey(f.Window, 9081));
            f.AssertReleased("emergency", 2, 2); Equal(1, guard.DisposeCalls);
            f.Window.ReleaseForError(); Equal(1, guard.DisposeCalls); f.AssertHistory();
        });

        foreach (var reason in new[] { SessionSwitchReason.SessionLock, SessionSwitchReason.SessionLogoff })
            Run("whitelist " + reason + " restores the workspace before asynchronous session persistence", () =>
            {
                using var f = new Fixture(); ConfigureWhitelist(f);
                var guard = new FakeWorkspaceSession();
                SetWorkspaceFactory(f, (_, _, _) => Task.FromResult<IFocusWorkspaceSession>(guard));
                f.Start(); f.Tick(2.1);
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var queue = Field<SerialWorkQueue>(f.Window, "focusWork");
                var blocker = queue.RunAsync(() => { entered.Set(); release.Wait(); }); WaitUntil(() => entered.IsSet);
                try
                {
                    Call(f.Window, "SessionChanged", f.Window, new SessionSwitchEventArgs(reason)); Pump();
                    Equal(1, guard.DisposeCalls); Assert(Field<object?>(f.Window, "clock") is null && !f.Bridge.active);
                    Assert(!Field<Task>(f.Window, "finishTask").IsCompleted);
                }
                finally { release.Set(); }
                Wait(blocker); f.AssertReleased("interrupted", 2, 2); f.AssertHistory();
            });

        Run("whitelist suspend releases its guard and a later resume cannot silently restart the session", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            var guard = new FakeWorkspaceSession();
            SetWorkspaceFactory(f, (_, _, _) => Task.FromResult<IFocusWorkspaceSession>(guard));
            f.Start(); f.Tick(2.1);
            Call(f.Window, "PowerChanged", f.Window, new PowerModeChangedEventArgs(PowerModes.Suspend)); Pump();
            f.AssertReleased("interrupted", 2, 2); Equal(1, guard.DisposeCalls);
            Call(f.Window, "PowerChanged", f.Window, new PowerModeChangedEventArgs(PowerModes.Resume)); Pump();
            Assert(Field<object?>(f.Window, "clock") is null && !f.Bridge.active);
            f.AssertHistory();
        });

        Run("workspace monitoring failure ends strict focus and preserves its saved seconds", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            var guard = new FakeWorkspaceSession();
            SetWorkspaceFactory(f, (_, _, _) => Task.FromResult<IFocusWorkspaceSession>(guard));
            f.Start(); f.Tick(2.1); guard.FailPulse = true;
            f.Tick(.01);
            f.AssertReleased("interrupted", 2, 2); Equal(1, guard.DisposeCalls); f.AssertHistory();
        });

        Run("a throwing workspace disposal cannot prevent session release or duplicate saved progress", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            var guard = new FakeWorkspaceSession { FailDispose = true };
            SetWorkspaceFactory(f, (_, _, _) => Task.FromResult<IFocusWorkspaceSession>(guard));
            f.Start(); f.Tick(2.1);
            f.Window.ReleaseForError();
            f.AssertReleased("interrupted", 2, 2); Equal(1, guard.DisposeCalls);
            Assert(Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession") is null);
            var saved = f.AllRows(); f.Window.ReleaseForError(); f.Drain(); Equal(saved, f.AllRows());
            Equal(1, guard.DisposeCalls); f.AssertHistory();
        });

        Run("break mode never creates a whitelist guard or hides system surfaces", () =>
        {
            using var f = new Fixture(); ConfigureWhitelist(f);
            SetWorkspaceFactory(f, (_, _, _) => throw new Exception("Break started a workspace guard."));
            var before = f.AllRows(); Call(f.Window, "StartBreak", f.Window, new RoutedEventArgs());
            Assert(Field<bool>(f.Window, "breaking") && Field<IFocusWorkspaceSession?>(f.Window, "workspaceSession") is null);
            Call(f.Window, "ConfirmEmergency"); Equal(before, f.AllRows()); Assert(!f.Bridge.active);
        });

        Run("hidden workspace dock switches multiple windows, retains selection and removes closed targets without enabling strict exit", () =>
        {
            using var f = new Fixture();
            var activated = new List<nint>(); int exits = 0;
            var dock = new WorkspaceDockWindow(activated.Add, () => throw new Exception("Unexpected main-window activation."), () => exits++, true);
            try
            {
                var editor = new WorkspaceWindowChoice((nint)101, "编辑器");
                var reader = new WorkspaceWindowChoice((nint)102, "阅读器");
                Equal("编辑器", editor.ToString()); Equal("阅读器", reader.ToString());
                dock.SetWindows([editor, reader]);
                var apps = Field<ComboBox>(dock, "apps");
                var switchButton = Field<Button>(dock, "switchApp");
                Assert(!dock.IsVisible && !Find<Button>(dock, "WorkspaceExitButton").IsEnabled);
                Equal(2, apps.Items.Count); apps.SelectedIndex = 1;
                switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(activated.SequenceEqual(new[] { (nint)102 }));
                dock.SetWindows([reader, editor]);
                Equal((nint)102, ((WorkspaceWindowChoice)apps.SelectedItem).Handle);
                dock.UpdateTime("00:42"); Equal("00:42", Field<TextBlock>(dock, "time").Text);
                Assert(ReferenceEquals(dock.FindResource("Text"), Field<TextBlock>(dock, "time").Foreground));
                Render(dock, "workspace-whitelist.png", 490, 170);
                dock.SetWindows([editor]);
                Equal((nint)101, ((WorkspaceWindowChoice)apps.SelectedItem).Handle);
                switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(activated.SequenceEqual(new[] { (nint)102, (nint)101 }));
                dock.SetWindows([]); Assert(!switchButton.IsEnabled && apps.SelectedItem is null);
                switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(2, activated.Count); Equal(0, exits); Pump(); f.AssertHistory();
            }
            finally { dock.Release(); }
            var ordinary = new WorkspaceDockWindow(_ => { }, () => { }, () => exits++, false);
            try
            {
                var end = Find<Button>(ordinary, "WorkspaceExitButton"); Assert(end.IsEnabled);
                end.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Equal(1, exits);
            }
            finally { ordinary.Release(); }
        });
    }

    private static void ConfigureWhitelist(Fixture fixture)
    {
        Field<CheckBox>(fixture.Window, "WhitelistEnabledCheck").IsChecked = true;
        Field<TextBox>(fixture.Window, "AllowedAppsInput").Text = "synthetic-editor.exe\r\nsynthetic-reader.exe";
        Field<CheckBox>(fixture.Window, "HideTaskbarCheck").IsChecked = true;
        Field<CheckBox>(fixture.Window, "BlockDesktopShortcutsCheck").IsChecked = true;
    }

    private static void SetWorkspaceFactory(Fixture fixture,
        Func<FocusApplicationPolicy, nint, CancellationToken, Task<IFocusWorkspaceSession>> factory)
        => SetField(fixture.Window, "workspaceFactory", factory);

    private sealed class FakeWorkspaceSession : IFocusWorkspaceSession
    {
        public int DisposeCalls, PulseCalls;
        public bool FailPulse, FailDispose;
        public void Pulse()
        {
            PulseCalls++;
            if (FailPulse) throw new IOException("synthetic workspace monitor failure");
        }
        public IReadOnlyList<FocusWorkspaceAppWindow> GetAllowedWindows() =>
            [new((nint)101, "synthetic-editor"), new((nint)102, "synthetic-reader")];
        public bool ActivateAllowedWindow(nint handle) => handle == (nint)101 || handle == (nint)102;
        public void Dispose()
        {
            DisposeCalls++;
            if (FailDispose) throw new IOException("synthetic workspace restore failure");
        }
    }

    private static void Soak(int minutes)
    {
        using var f = new Fixture();
        Field<TextBox>(f.Window, "FocusMinutesInput").Text = minutes.ToString();
        var tracker = Field<object>(f.Window, "usageTracker");
        var viewType = typeof(MainWindow).Assembly.GetType("FocusGarden.App.PlanetView3D", true)!;
        var scene = Activator.CreateInstance(viewType, [true])!;
        string[] kinds = ["rabbit", "rabbit", "sheep", "sheep", "fox", "fox", "duck", "duck", "deer", "deer", "butterfly", "butterfly", "pond", "pond", "flower", "flower", "meadow", "meadow", "bush", "bush", "tree", "tree", "orchard", "orchard", "mushroom", "mushroom", "rockgarden", "rockgarden", "house", "windmill", "tower", "observatory"];
        var layout = kinds.Select((kind, index) => new PlanetBuildingPlacement("soak-" + index, kind, index, index % 4)).ToArray();
        Call(scene, "ApplyState", 5, "rain", false, false, 42, layout, false, null!);
        var sceneBuilds = Property<int>(scene, "SceneBuildCount");
        var animationCpu = new List<double>(); var editCpu = new List<double>(); var heartbeatDelay = new List<double>();
        var samples = new List<object>(); var started = DateTimeOffset.Now; var runtime = Stopwatch.StartNew();
        f.Start();
        int page = 0, edits = 0, usageSeconds = 0, animationCalls = 0, tokenImports = 0;
        double nextPage = 10, nextUsage = 5, nextReport = 60, nextAnimation = 0, nextToken = 30;
        var tokenLog = Path.Combine(f.DirectoryPath, "empty-codex", "sessions", "soak.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenLog)!);
        long peakPrivateBytes = 0;
        var timer = Field<DispatcherTimer>(f.Window, "timer"); timer.Start();
        Console.WriteLine($"SOAK START pid={Environment.ProcessId} minutes={minutes} data={f.DirectoryPath} assembly={typeof(MainWindow).Assembly.Location}");
        try
        {
            while (Field<object?>(f.Window, "clock") is not null)
            {
                var elapsed = runtime.Elapsed.TotalSeconds;
                if (elapsed > minutes * 60 + 60) throw new TimeoutException("Real-time focus did not complete within the allowed UI scheduling margin.");
                var pulseAt = Stopwatch.GetTimestamp();
                var pulse = f.Window.Dispatcher.InvokeAsync(() =>
                {
                    heartbeatDelay.Add(Stopwatch.GetElapsedTime(pulseAt).TotalMilliseconds);
                }, DispatcherPriority.Background);
                Wait(pulse.Task);
                if (Field<object?>(f.Window, "clock") is null) break;
                if (elapsed >= nextAnimation)
                {
                    var watch = Stopwatch.StartNew(); Call(scene, "AnimateScene", elapsed); watch.Stop();
                    animationCpu.Add(watch.Elapsed.TotalMilliseconds); animationCalls++; nextAnimation = elapsed + .12;
                }
                if (elapsed >= nextUsage)
                {
                    // Synthetic samples enter the same accumulator as Win32 sampling. Native tracking remains disabled.
                    var now = DateTimeOffset.Now; var tick = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                    Call(tracker, "Observe", new AppUsageSample("soak-editor.exe", tick, now, 0, true, false));
                    Call(tracker, "Observe", new AppUsageSample("soak-editor.exe", tick + 1, now.AddSeconds(1), 0, true, false));
                    usageSeconds++; nextUsage = elapsed + 5;
                }
                if (elapsed >= nextToken)
                {
                    tokenImports++;
                    var total = new { input_tokens = tokenImports * 100, output_tokens = 0, cached_input_tokens = 0, reasoning_output_tokens = 0, total_tokens = tokenImports * 100 };
                    var last = new { input_tokens = 100, output_tokens = 0, cached_input_tokens = 0, reasoning_output_tokens = 0, total_tokens = 100 };
                    File.AppendAllText(tokenLog, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now.ToString("O"), type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = total, last_token_usage = last } } }) + Environment.NewLine);
                    Assert((bool)Call(f.Window, "ImportTokensAsync")!); nextToken = elapsed + 30;
                }
                if (elapsed >= nextPage)
                {
                    Call(f.Window, "Page", new[] { "focus", "stats", "planet" }[page++ % 3]);
                    Call(scene, "SetSurfaceView", page % 2 == 0); Call(scene, "RotateView", 12.0, page % 2 == 0 ? 3.0 : -3.0);
                    Call(scene, "SetEditMode", true);
                    var watch = Stopwatch.StartNew();
                    Call(scene, "SelectPlot", edits % 32);
                    var target = edits % layout.Length; layout[target] = layout[target] with { QuarterTurns = (layout[target].QuarterTurns + 1) % 4 };
                    Call(scene, "SetBuildings", (object)layout); watch.Stop(); editCpu.Add(watch.Elapsed.TotalMilliseconds);
                    Call(scene, "SetEditMode", false); edits++; nextPage = elapsed + 10;
                    Equal(sceneBuilds, Property<int>(scene, "SceneBuildCount"));
                }
                if (elapsed >= nextReport)
                {
                    f.Drain(); f.AssertHistory();
                    var current = f.Current;
                    Equal((long)current.ElapsedSeconds, f.CurrentDaySeconds());
                    Assert(current.Status == "running" || current.Status == "completed" && current.ElapsedSeconds == minutes * 60);
                    using var process = Process.GetCurrentProcess(); process.Refresh(); peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
                    samples.Add(new { ElapsedSeconds = elapsed, PersistedFocusSeconds = current.ElapsedSeconds, PrivateBytes = process.PrivateMemorySize64, ManagedBytes = GC.GetTotalMemory(false), Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2) });
                    Console.WriteLine($"SOAK {elapsed / 60:0.0} min focus={current.ElapsedSeconds}s private={process.PrivateMemorySize64 / 1048576.0:0.0}MiB sceneBuilds={Property<int>(scene, "SceneBuildCount")} edits={edits}");
                    nextReport = elapsed + 60;
                }
                Thread.Sleep(20);
            }
        }
        finally { timer.Stop(); }
        f.Drain();
        f.AssertReleased("completed", minutes * 60, minutes * 60); f.AssertHistory();
        var saved = f.AllRows(); f.Drain(); Equal(saved, f.AllRows());
        var usage = new[] { started.LocalDateTime.Date, DateTime.Today }.Distinct().SelectMany(day => Field<AppUsageStore>(f.Window, "usageStore").GetDailyUsage(day)).Where(row => row.AppName == "soak-editor.exe").Sum(row => row.Seconds);
        Equal(usageSeconds, usage);
        var tokens = Field<CodexUsageImporter>(f.Window, "tokenImporter").GetTotals().TotalTokens;
        Equal(tokenImports * 100L, tokens);
        Equal(sceneBuilds, Property<int>(scene, "SceneBuildCount"));
        double P95(List<double> values) => values.Count == 0 ? 0 : values.Order().ElementAt((int)Math.Floor((values.Count - 1) * .95));
        var result = new
        {
            StartedAt = started, FinishedAt = DateTimeOffset.Now, DurationSeconds = runtime.Elapsed.TotalSeconds,
            DataDirectory = f.DirectoryPath, ProcessId = Environment.ProcessId, FocusSeconds = f.Current.ElapsedSeconds,
            UsageSeconds = usage, TokenImports = tokenImports, ImportedTokens = tokens, HistoricalRowsUnchanged = true, DuplicateCredit = false,
            NaturalSceneRebuildsDuringLocalEdits = Property<int>(scene, "SceneBuildCount") - sceneBuilds,
            AnimationCalls = animationCalls, LocalEdits = edits, AnimationCpuP95Ms = P95(animationCpu), LocalEditCpuP95Ms = P95(editCpu),
            DispatcherPostToCallbackP95Ms = P95(heartbeatDelay), DispatcherPostToCallbackMaximumMs = heartbeatDelay.Count == 0 ? 0 : heartbeatDelay.Max(),
            PeakPrivateBytes = peakPrivateBytes, Samples = samples,
            Limitations = "Hidden synthetic windows; model-update CPU and dispatcher scheduling only, not GPU frame rate or interactive user input."
        };
        var resultPath = Path.Combine(f.DirectoryPath, "soak-result.json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SOAK RESULT " + resultPath); Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath;
        public readonly MainWindow Window;
        private readonly string history;
        private bool closed;
        public FocusStore Store => Field<FocusStore>(Window, "store");
        public BridgeState Bridge => Field<BridgeState>(Field<LocalBridge>(Window, "bridge"), "state");
        public FocusSession Current => Store.GetSessions().Single(s => s.Task == "strict test");
        public Fixture(bool strict = true, bool legacyJson = false)
        {
            DirectoryPath = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
            var emptyCodex = Path.Combine(DirectoryPath, "empty-codex"); Directory.CreateDirectory(emptyCodex);
            File.WriteAllText(Path.Combine(DirectoryPath, "dashboard.json"), JsonSerializer.Serialize(new { TrackApplications = false, AutoRefreshTokens = false, CodexHome = emptyCodex }));
            using (var store = new FocusStore(Path.Combine(DirectoryPath, "focus.db")))
            {
                store.SaveSettings(new FocusSettings { FocusMinutes = 1, BreakMinutes = 1, Immersive = false, StrictFocus = strict, BlockedApps = [], BlockedDomains = ["example.com"] });
                var start = new DateTimeOffset(2025, 12, 31, 23, 59, 45, TimeSpan.Zero).ToLocalTime();
                var id = store.BeginSession("historic", 60, start); store.AddProgress(id, 60, start.AddSeconds(60)); store.EndSession(id, "completed", start.AddSeconds(60));
            }
            if (legacyJson)
            {
                using var db = new SqliteConnection("Data Source=" + Path.Combine(DirectoryPath, "focus.db")); db.Open();
                using var c = db.CreateCommand(); c.CommandText = "UPDATE settings SET json=$json WHERE id=1";
                c.Parameters.AddWithValue("$json", "{\"FocusMinutes\":1,\"BreakMinutes\":1,\"Immersive\":false,\"BlockedApps\":[],\"BlockedDomains\":[]}"); c.ExecuteNonQuery();
            }
            history = Snapshot(historicalOnly: true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, DirectoryPath);
            Window = new MainWindow(); Field<DispatcherTimer>(Window, "timer").Stop();
            Window.Closed += (_, _) => closed = true;
            Field<TextBox>(Window, "TaskInput").Text = "strict test";
            Confirm(_ => true);
            Drain();
        }
        public void Confirm(Func<int, bool> answer) => SetField(Window, "strictStartConfirmation", answer);
        public void Start() { Call(Window, "Begin", false); Drain(); Assert(Field<object?>(Window, "clock") is not null); }
        public void Drain() => Call(Window, "WaitForBackgroundAsync");
        public void DrainFocus() => Call(Window, "DrainFocusAsync");
        public void Tick(double seconds, bool waitForFocus = true)
        {
            SetField(Window, "lastTimestamp", Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * seconds));
            Call(Window, "Tick", null!, EventArgs.Empty);
            if (waitForFocus) DrainFocus();
        }
        public long CurrentDaySeconds()
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(DirectoryPath, "focus.db")); db.Open();
            using var c = db.CreateCommand(); c.CommandText = "SELECT COALESCE(SUM(seconds),0) FROM session_days WHERE session_id=$id";
            c.Parameters.AddWithValue("$id", Current.Id); return (long)c.ExecuteScalar()!;
        }
        public void AssertReleased(string status, int seconds, long days)
        {
            DrainFocus();
            Assert(Field<object?>(Window, "clock") is null); Assert(!Field<bool>(Window, "activeStrictFocus"));
            Assert(!Bridge.active); Equal(status, Current.Status); Equal(seconds, Current.ElapsedSeconds); Equal(days, CurrentDaySeconds());
            Assert(Field<FrameworkElement>(Window, "SetupPanel").IsEnabled);
        }
        public string AllRows() => Snapshot(false);
        public void AssertHistory() => Equal(history, Snapshot(true));
        private string Snapshot(bool historicalOnly)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(DirectoryPath, "focus.db")); db.Open();
            var tables = new List<object>();
            foreach (var sql in new[] { "SELECT * FROM sessions" + (historicalOnly ? " WHERE task='historic'" : "") + " ORDER BY id", "SELECT * FROM session_days" + (historicalOnly ? " WHERE session_id IN (SELECT id FROM sessions WHERE task='historic')" : "") + " ORDER BY session_id,day" })
            {
                using var c = db.CreateCommand(); c.CommandText = sql; using var r = c.ExecuteReader(); var rows = new List<object[]>();
                while (r.Read()) { var values = new object[r.FieldCount]; r.GetValues(values); rows.Add(values); }
                tables.Add(rows);
            }
            return JsonSerializer.Serialize(tables);
        }
        public void Dispose()
        {
            if (closed) return;
            Field<DispatcherTimer>(Window, "timer").Stop();
            Window.ReleaseForError(); Call(Window, "PrepareCloseAsync"); Window.Close();
        }
    }

    private static bool SendHotkey(MainWindow window, int id)
    {
        object[] args = [nint.Zero, 0x0312, (nint)id, nint.Zero, false];
        typeof(MainWindow).GetMethod("WindowHook", Private)!.Invoke(window, args);
        Call(window, "DrainFocusAsync");
        return (bool)args[4];
    }
    private static Window CreateImmersion(bool strict, Action callback)
    {
        var type = typeof(App).Assembly.GetType("FocusGarden.App.ImmersionWindow", throwOnError: true)!;
        return (Window)Activator.CreateInstance(type, ["阅读一节教材", true, null, callback, strict])!;
    }
    private static KeyEventArgs Escape(Window window)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
        window.RaiseEvent(key); return key;
    }
    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    // Pumping synthetic system events must never dispatch production startup or its default data directory.
    private sealed class TestApplication : Application
    {
        public TestApplication()
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("StrictFocusTests.ProductionApp.xaml")!;
            using var reader = new StreamReader(stream);
            var document = XElement.Parse(reader.ReadToEnd());
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var dictionary = new XElement(ns + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                document.Element(ns + "Application.Resources")!.Elements().Select(element => new XElement(element)));
            Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        }
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private static T Find<T>(DependencyObject item, string name) where T : FrameworkElement
    {
        if (item is T match && match.Name == name) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
        {
            try { return Find<T>(child, name); } catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(name);
    }
    private static void Pump()
    {
        var expectedDataDirectory = App.DataDirectory;
        Assert(Path.GetFullPath(expectedDataDirectory).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Equal(expectedDataDirectory, App.DataDirectory);
        Assert(Application.Current.Windows.OfType<Window>().All(window => !window.IsVisible));
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(instance)!;
    private static SqliteConnection OpenWriterLock(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "BEGIN IMMEDIATE"; command.ExecuteNonQuery();
        return connection;
    }
    private static void Rollback(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
    }
    private static Dictionary<string, StackPanel> UsagePanels(MainWindow window)
        => Field<StackPanel>(window, "UsageRows").Children.OfType<StackPanel>().ToDictionary(
            panel => ((TextBlock)((DockPanel)panel.Children[0]).Children[1]).ToolTip.ToString()!, panel => panel);
    private static void SetField(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object[] values)
        => instance.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(instance, values);
    private static object? Call(object instance, string name, params object[] values)
    {
        if (instance is MainWindow && name == "RefreshPlanet") name = "RefreshPlanetAsync";
        var wasStarting = instance is MainWindow && Field<bool>(instance, "startingFocus");
        var result = Invoke(instance, name, values);
        if (result is Task task)
        {
            Wait(task);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        // The production click handler remains async void; await its actual work without
        // changing its code path or pumping recursively inside strict confirmation.
        if (instance is MainWindow && name == "StartBreak" && !wasStarting)
            WaitUntil(() => !Field<bool>(instance, "startingFocus"));
        return result;
    }
    private static void Wait(Task task, int timeoutSeconds = 30)
    {
        WaitUntil(() => task.IsCompleted, timeoutSeconds);
        task.GetAwaiter().GetResult();
    }
    private static void WaitUntil(Func<bool> completed, int timeoutSeconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException("Synthetic dispatcher wait exceeded its deadline.");
            Pump(); Thread.Sleep(1);
        }
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Render(Window window, string name, int width = 1380, int height = 1040)
    {
        if (renderDirectory is null) return;
        var content = (FrameworkElement)window.Content; window.Content = null;
        var container = new Border { Background = window.Background, Child = content, Width = width, Height = height };
        try
        {
            container.Measure(new Size(width, height)); container.Arrange(new Rect(0, 0, width, height)); container.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(container);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(renderDirectory); using var stream = File.Create(Path.Combine(renderDirectory, name)); encoder.Save(stream);
        }
        finally { container.Child = null; window.Content = content; }
    }
}
