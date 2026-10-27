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

    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath;
        public readonly MainWindow Window;
        private readonly string history;
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
            Field<TextBox>(Window, "TaskInput").Text = "strict test";
            Confirm(_ => true);
        }
        public void Confirm(Func<int, bool> answer) => SetField(Window, "strictStartConfirmation", answer);
        public void Start() { Call(Window, "Begin", false); Assert(Field<object?>(Window, "clock") is not null); }
        public void Tick(double seconds)
        {
            SetField(Window, "lastTimestamp", Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * seconds));
            Call(Window, "Tick", null!, EventArgs.Empty);
        }
        public long CurrentDaySeconds()
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(DirectoryPath, "focus.db")); db.Open();
            using var c = db.CreateCommand(); c.CommandText = "SELECT COALESCE(SUM(seconds),0) FROM session_days WHERE session_id=$id";
            c.Parameters.AddWithValue("$id", Current.Id); return (long)c.ExecuteScalar()!;
        }
        public void AssertReleased(string status, int seconds, long days)
        {
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
            Field<DispatcherTimer>(Window, "timer").Stop();
            Window.ReleaseForError(); Window.Close();
        }
    }

    private static bool SendHotkey(MainWindow window, int id)
    {
        object[] args = [nint.Zero, 0x0312, (nint)id, nint.Zero, false];
        typeof(MainWindow).GetMethod("WindowHook", Private)!.Invoke(window, args);
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
    private static void SetField(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object[] values)
        => instance.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(instance, values);
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
