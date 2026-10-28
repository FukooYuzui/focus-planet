using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FocusGarden.App;
using FocusGarden.Core;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        // All data is synthetic and isolated. No window is shown and no desktop input is sent.
        var root = Path.Combine(Path.GetTempPath(), "FocusPlanet-Integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new TestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int passed = 0, failed = 0;
        void Run(string name, Action body)
        { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
        var first = Path.Combine(root, "fresh"); Directory.CreateDirectory(first);
        using (var focus = new FocusStore(Path.Combine(first, "focus.db")))
        {
            var start = DateTimeOffset.Now.AddHours(-2); var id = focus.BeginSession("验收历史记录", 3600, start);
            focus.AddProgress(id, 3600, start.AddHours(1)); focus.EndSession(id, "completed", start.AddHours(1));
        }
        SetDataDirectory(first);
        var fresh = new MainWindow();
        PrepareWindow(fresh);
        try
        {
            Run("new planet ignores historical focus and starts barren", () =>
            {
                var s = Field<PlanetSnapshot>(fresh, "planetSnapshot");
                Assert(s.TotalGrowth == 0 && s.Level == 0);
                Assert(Field<FrameworkElement>(fresh, "PlanetPage").Visibility == Visibility.Visible);
                Assert(fresh.Title.Contains("专注星球 " + App.CurrentVersion));
                var view = Field<object>(fresh, "planetView");
                Assert((int)view.GetType().GetProperty("SceneBuildCount", Flags)!.GetValue(view)! == 1);
            });
            Run("new focus feeds planet once and page navigation preserves timer setup", () =>
            {
                var store = Field<FocusStore>(fresh, "store"); var end = DateTimeOffset.Now;
                var id = store.BeginSession("验收新增专注", 120, end.AddMinutes(-2)); store.AddProgress(id, 120, end); store.EndSession(id, "completed", end);
                Call(fresh, "ForceRefreshPlanetAsync"); Assert(Field<PlanetSnapshot>(fresh, "planetSnapshot").TotalGrowth == 2);
                Call(fresh, "RefreshPlanet"); Assert(Field<PlanetSnapshot>(fresh, "planetSnapshot").TotalGrowth == 2);
                Call(fresh, "Page", "focus"); Assert(Field<FrameworkElement>(fresh, "FocusPage").Visibility == Visibility.Visible);
                Assert(Field<FrameworkElement>(fresh, "PlanetPage").Visibility == Visibility.Collapsed);
                Call(fresh, "Page", "planet");
            });
            Run("clock and next-stage requirement appear on planet page", () =>
            {
                Call(fresh, "PlanetTick"); Assert(Field<TextBlock>(fresh, "PlanetClockText").Text.Length == 8);
                Assert(Field<TextBlock>(fresh, "PlanetNextText").Text.Contains("森林萌芽"));
                Assert(!Field<Button>(fresh, "ExplorePlanetButton").IsEnabled);
            });
            Run("locked growth database leaves dispatcher responsive and retry awards focus once", () =>
            {
                AddFocus(fresh, 60);
                using var blocker = new SqliteConnection("Data Source=" + Path.Combine(first, "planet.db")); blocker.Open();
                using var transaction = blocker.BeginTransaction();
                var started = System.Diagnostics.Stopwatch.StartNew();
                var refresh = (Task<bool>)Invoke(fresh, "RefreshPlanetAsync")!;
                Assert(started.Elapsed < TimeSpan.FromMilliseconds(500));
                bool responsive = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(() => responsive = true);
                PumpFor(TimeSpan.FromMilliseconds(150));
                Assert(responsive && !refresh.IsCompleted);
                transaction.Commit(); Await(refresh); Assert(refresh.Result);
                Assert(Field<PlanetSnapshot>(fresh, "planetSnapshot").TotalGrowth == 3);
                Call(fresh, "RefreshPlanetAsync"); Assert(Field<PlanetSnapshot>(fresh, "planetSnapshot").TotalGrowth == 3);
                Assert(Field<FocusStore>(fresh, "store").GetAllDailyTotals().Values.Sum() == 3780);
            });
            Run("unchanged refresh reads through a held writer lock without requesting a write", () =>
            {
                using var blocker = new SqliteConnection("Data Source=" + Path.Combine(first, "planet.db")); blocker.Open();
                using var transaction = blocker.BeginTransaction();
                var refresh = (Task<bool>)Invoke(fresh, "RefreshPlanetAsync")!; Await(refresh);
                Assert(refresh.Result && Field<PlanetSnapshot>(fresh, "planetSnapshot").TotalGrowth == 3);
                transaction.Rollback();
            });
            Run("hidden planet applies cached updates immediately on return without resetting camera", () =>
            {
                var view = Field<object>(fresh, "planetView");
                view.GetType().GetMethod("SetSurfaceView")!.Invoke(view, new object[] { true });
                Call(view, "SetZoom", .94);
                Call(fresh, "Page", "focus"); AddFocus(fresh, 60); Call(fresh, "RefreshPlanetAsync");
                Assert(Field<bool>(fresh, "planetPresentationDirty"));
                Call(fresh, "Page", "planet"); Call(fresh, "PlanetTick");
                Assert(!Field<bool>(fresh, "planetPresentationDirty"));
                Assert(Field<TextBlock>(fresh, "PlanetGrowthText").Text.Contains("4"));
                Assert((bool)view.GetType().GetProperty("IsSurfaceView", Flags)!.GetValue(view)!);
                Assert(Math.Abs((double)view.GetType().GetProperty("Zoom", Flags)!.GetValue(view)! - .94) < 1e-9);
            });
            if (args.Length == 2 && args[0] == "--render") Render(fresh, Path.Combine(args[1], "专注星球-初生预览.png"));
        }
        finally { CloseWindow(fresh); }

        foreach (var failPresentation in new[] { false, true })
        Run(failPresentation
            ? "close never replays a committed rotation when presentation read fails"
            : "failed layout write keeps close pending and explicit retry rotates exactly once", () =>
        {
            var path = Path.Combine(root, failPresentation ? "closing-read-failure" : "closing-write-failure");
            Directory.CreateDirectory(path); SetDataDirectory(path);
            var window = new MainWindow(); PrepareWindow(window);
            try
            {
                AddFocus(window, 60);
                var before = Field<FocusStore>(window, "store").GetAllDailyTotals();
                var id = Field<PlanetSnapshot>(window, "planetSnapshot").PlanetId.ToString();
                Field<ComboBox>(window, "PlanetBuildingChoice").SelectedValue = "rockgarden";
                Event(window, "StartPlanetPlacement"); Call(window, "PlanetPlotClicked", 0);
                using var blocker = new SqliteConnection("Data Source=" + Path.Combine(path, "planet-layout.db")); blocker.Open();
                using var transaction = blocker.BeginTransaction();
                // The save can succeed even though the subsequent independent growth snapshot read fails.
                if (failPresentation) Field<PlanetGrowthStore>(window, "planetStore").Dispose();
                Invoke(window, "RotatePlanetBuilding", window, new RoutedEventArgs());
                var close = (Task)Invoke(window, "PrepareCloseAsync")!;
                if (failPresentation)
                {
                    PumpFor(TimeSpan.FromMilliseconds(150)); Assert(!close.IsCompleted);
                    transaction.Commit(); Await(close);
                }
                else
                {
                    IOException? failure = null;
                    try { Await(close); } catch (IOException ex) { failure = ex; }
                    Assert(failure != null && failure.Message.Contains("家园布局"));
                    Assert(!Field<bool>(window, "allowClose") && !Field<bool>(window, "planetDisposed"));
                    Assert(Field<Action?>(window, "pendingClosePlanetMutation") != null);
                    Assert(Field<PlanetLayoutStore>(window, "planetLayout").GetBuildings(id).Single().QuarterTurns == 0);
                    transaction.Commit();
                    Await((Task)Invoke(window, "PrepareCloseAsync")!);
                }
                Assert(Field<Action?>(window, "pendingClosePlanetMutation") == null);
                // A further close request is idempotent, including non-idempotent rotation operations.
                Await((Task)Invoke(window, "PrepareCloseAsync")!);
                using var layout = new PlanetLayoutStore(Path.Combine(path, "planet-layout.db"));
                Assert(layout.GetBuildings(id).Single().QuarterTurns == 1);
                using var focus = new FocusStore(Path.Combine(path, "focus.db"));
                Assert(before.OrderBy(p => p.Key).SequenceEqual(focus.GetAllDailyTotals().OrderBy(p => p.Key)));
            }
            finally { CloseWindow(window); }
        });

        var maturePath = Path.Combine(root, "mature"); Directory.CreateDirectory(maturePath);
        long planetId;
        using (var growth = new PlanetGrowthStore(Path.Combine(maturePath, "planet.db")))
        {
            var start = DateTimeOffset.Now.AddMonths(-6); growth.Initialize(start, new Dictionary<string, int>());
            growth.SetName("星海家园 · 演示");
            var focus = Enumerable.Range(0, 80).ToDictionary(n => start.AddDays(n).ToLocalTime().ToString("yyyy-MM-dd"), _ => 7200);
            planetId = growth.Update(DateTimeOffset.Now, focus, new Dictionary<string, long>()).PlanetId;
        }
        using (var layout = new PlanetLayoutStore(Path.Combine(maturePath, "planet-layout.db")))
        {
            foreach (var (kind, plot) in new[] { ("house", 0), ("house", 2), ("house", 6), ("windmill", 4), ("tower", 9), ("observatory", 1), ("tree", 11), ("flower", 12) }) layout.Place(planetId.ToString(), kind, plot, 5);
        }
        SetDataDirectory(maturePath); var mature = new MainWindow(); PrepareWindow(mature);
        try
        {
            Run("six-month mature fixture restores growth and independent layout", () =>
            {
                var s = Field<PlanetSnapshot>(mature, "planetSnapshot"); Assert(s.Level == 5 && s.CanExplore && s.TotalGrowth == 9600);
                Assert(Field<IReadOnlyList<PlanetBuildingPlacement>>(mature, "planetBuildings").Count == 8);
            });
            Run("rename is persisted and invalid name leaves previous name intact", () =>
            {
                Field<TextBox>(mature, "PlanetNameInput").Text = "星海家园 · 演示"; Event(mature, "RenamePlanet");
                Assert(Field<PlanetSnapshot>(mature, "planetSnapshot").Name == "星海家园 · 演示");
                Field<TextBox>(mature, "PlanetNameInput").Text = " "; Event(mature, "RenamePlanet");
                Assert(Field<PlanetGrowthStore>(mature, "planetStore").GetSnapshot(DateTimeOffset.Now).Name == "星海家园 · 演示");
                Field<TextBox>(mature, "PlanetNameInput").Text = "星海家园 · 演示";
            });
            Run("placement rotate move and remove actions persist", () =>
            {
                Field<ComboBox>(mature, "PlanetBuildingChoice").SelectedValue = "tree"; Event(mature, "StartPlanetPlacement");
                Call(mature, "PlanetPlotClicked", 15);
                var placed = Field<IReadOnlyList<PlanetBuildingPlacement>>(mature, "planetBuildings").Single(b => b.PlotId == 15);
                Event(mature, "RotatePlanetBuilding"); Event(mature, "MovePlanetBuilding"); Call(mature, "PlanetPlotClicked", 16);
                using var persisted = new PlanetLayoutStore(Path.Combine(maturePath, "planet-layout.db"));
                Assert(persisted.GetBuildings(planetId.ToString()).Single(b => b.Id == placed.Id) is { PlotId: 16, QuarterTurns: 1 });
                Event(mature, "RemovePlanetBuilding"); Assert(persisted.GetBuildings(planetId.ToString()).All(b => b.Id != placed.Id));
            });
            Run("failed occupied move preserves original selected building", () =>
            {
                Call(mature, "PlanetPlotClicked", 0); Event(mature, "MovePlanetBuilding"); Call(mature, "PlanetPlotClicked", 2);
                Assert(Field<int?>(mature, "selectedPlanetPlot") == 0);
                Assert(Field<IReadOnlyList<PlanetBuildingPlacement>>(mature, "planetBuildings").Count == 8);
                Assert(Field<TextBlock>(mature, "PlanetLayoutHint").Text == Field<TextBlock>(mature, "StatusText").Text);
                Field<CheckBox>(mature, "PlanetEditCheck").IsChecked = false; Event(mature, "TogglePlanetEdit");
            });
            Run("growth refresh preserves unsubmitted rename and dropdown selection", () =>
            {
                Field<TextBox>(mature, "PlanetNameInput").Text = "未提交的名字";
                Call(mature, "RefreshPlanet"); Assert(Field<TextBox>(mature, "PlanetNameInput").Text == "未提交的名字");
                Field<TextBox>(mature, "PlanetNameInput").Text = "星海家园 · 演示";
            });
            Run("expanded viewing and editing reuse the live scene and restore its parent", () =>
            {
                var host = Field<ContentControl>(mature, "PlanetSceneHost"); var original = host.Content;
                var card = Field<Border>(mature, "PlanetLayoutCard"); var parent = card.Parent;
                var liveView=Field<object>(mature,"planetView");
                void SetSurface(bool enabled)=>liveView.GetType().GetMethod("SetSurfaceView")!.Invoke(liveView,new object[]{enabled});
                bool IsSurface()=>(bool)liveView.GetType().GetProperty("IsSurfaceView",Flags)!.GetValue(liveView)!;
                double CurrentZoom()=>(double)liveView.GetType().GetProperty("Zoom",Flags)!.GetValue(liveView)!;
                SetSurface(true);Call(liveView,"SetZoom",.94);
                foreach (bool edit in new[] { false, true })
                {
                    var expanded = (Window)mature.GetType().GetMethod("CreatePlanetWorkspace", Flags)!.Invoke(mature, new object[] { edit })!;
                    Assert(host.Content == null && !expanded.IsVisible);
                    if (args.Length == 2 && args[0] == "--render") Render(expanded, Path.Combine(args[1], edit ? "专注星球-展开布置演示.png" : "专注星球-展开观景演示.png"), 1480, 960);
                    expanded.Close(); Assert(ReferenceEquals(host.Content, original) && ReferenceEquals(card.Parent, parent));
                    Assert(IsSurface() && Math.Abs(CurrentZoom()-.94)<1e-9);
                }
                Call(mature,"Page","focus");Call(mature,"Page","planet");Assert(IsSurface());
                SetSurface(false);
            });
            Run("ecology choices show categories and save all twelve new kinds", () =>
            {
                var choice=Field<ComboBox>(mature,"PlanetBuildingChoice");
                Assert(choice.Items.Count==18);
                var used=Field<IReadOnlyList<PlanetBuildingPlacement>>(mature,"planetBuildings").Select(b=>b.PlotId).ToHashSet();
                var free=new Queue<int>(Enumerable.Range(0,32).Where(p=>!used.Contains(p)));
                foreach(var kind in new[]{"pond","meadow","mushroom","orchard","rockgarden","bush","rabbit","sheep","fox","duck","deer","butterfly"})
                {
                    choice.SelectedValue=kind;
                    Assert(Field<TextBlock>(mature,"PlanetLayoutHint").Text.Contains(PlanetLayoutCatalog.Get(kind).Hint));
                    Event(mature,"StartPlanetPlacement");Call(mature,"PlanetPlotClicked",free.Dequeue());
                }
                using var persisted=new PlanetLayoutStore(Path.Combine(maturePath,"planet-layout.db"));
                Assert(persisted.GetBuildings(planetId.ToString()).Count==20);
                Assert(persisted.GetBuildings(planetId.ToString()).Count(b=>PlanetLayoutCatalog.IsAnimal(b.Kind))==6);
                Field<CheckBox>(mature,"PlanetEditCheck").IsChecked=false;Event(mature,"TogglePlanetEdit");
            });
            Run("failed growth commit prevents travel and later retry credits original planet", () =>
            {
                var prefs = Field<object>(mature, "dashboardPreferences");
                prefs.GetType().GetProperty("CodexHome")!.SetValue(prefs, Path.Combine(root, "missing-source"));
                var now = DateTimeOffset.Now; AddFocus(mature, 60);
                using var db = new SqliteConnection("Data Source=" + Path.Combine(maturePath, "planet.db")); db.Open();
                using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER qa_fail_credit BEFORE INSERT ON planet_growth_credits BEGIN SELECT RAISE(ABORT, 'QA simulated write failure'); END;"; cmd.ExecuteNonQuery();
                bool travelled = false;
                var task = (Task)mature.GetType().GetMethod("TravelPlanetAsync", Flags)!.Invoke(mature, new object[] { (Action)(() => travelled = true) })!;
                Await(task); Assert(!travelled);
                Assert(Field<PlanetGrowthStore>(mature, "planetStore").GetSnapshot(now).TotalGrowth == 9600);
                cmd.CommandText = "DROP TRIGGER qa_fail_credit"; cmd.ExecuteNonQuery();
                Call(mature, "RefreshPlanet"); var s = Field<PlanetSnapshot>(mature, "planetSnapshot"); Assert(s.PlanetId == planetId && s.TotalGrowth == 9601);
            });
            Call(mature, "PlanetTick");
            Call(mature, "Status", "离屏演示画面 · 使用独立合成数据，未改变真实进度");
            if (args.Length == 2 && args[0] == "--render") Render(mature, Path.Combine(args[1], "专注星球-成熟家园演示.png"));
            Field<CheckBox>(mature, "PlanetEditCheck").IsChecked = true; Event(mature, "TogglePlanetEdit");
            if (args.Length == 2 && args[0] == "--render") Render(mature, Path.Combine(args[1], "专注星球-布局演示.png"));
            Run("closing waits for accepted layout write and preserves independent saved focus", () =>
            {
                var before = Field<FocusStore>(mature, "store").GetAllDailyTotals();
                Field<ComboBox>(mature, "PlanetBuildingChoice").SelectedValue = "flower"; Event(mature, "StartPlanetPlacement");
                var used = Field<IReadOnlyList<PlanetBuildingPlacement>>(mature, "planetBuildings").Select(b => b.PlotId).ToHashSet();
                var plot = Enumerable.Range(0, 32).First(p => !used.Contains(p));
                using var blocker = new SqliteConnection("Data Source=" + Path.Combine(maturePath, "planet-layout.db")); blocker.Open();
                using var transaction = blocker.BeginTransaction();
                Invoke(mature, "PlanetPlotClicked", plot);
                var close = (Task)Invoke(mature, "PrepareCloseAsync")!;
                bool responsive = false; Dispatcher.CurrentDispatcher.BeginInvoke(() => responsive = true);
                PumpFor(TimeSpan.FromMilliseconds(150)); Assert(responsive && !close.IsCompleted);
                transaction.Commit(); Await(close);
                using var layout = new PlanetLayoutStore(Path.Combine(maturePath, "planet-layout.db"));
                Assert(layout.GetBuildings(planetId.ToString()).Any(b => b.PlotId == plot && b.Kind == "flower"));
                using var focus = new FocusStore(Path.Combine(maturePath, "focus.db"));
                Assert(before.OrderBy(p => p.Key).SequenceEqual(focus.GetAllDailyTotals().OrderBy(p => p.Key)));
            });
        }
        finally { CloseWindow(mature); app.Shutdown(); }
        Console.WriteLine($"{passed} passed / {failed} failed; isolated fixtures: {root}");
        return failed == 0 ? 0 : 1;
    }
    private static void SetDataDirectory(string path)
    {
        var emptyCodex = Path.Combine(path, "empty-codex"); Directory.CreateDirectory(emptyCodex);
        File.WriteAllText(Path.Combine(path, "dashboard.json"), JsonSerializer.Serialize(new { TrackApplications = false, AutoRefreshTokens = false, CodexHome = emptyCodex }));
        typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, path);
    }
    // Use production resources without running App.OnStartup or opening the real data directory.
    private sealed class TestApplication : Application
    {
        public TestApplication()
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("PlanetIntegrationTests.ProductionApp.xaml")!;
            using var reader = new StreamReader(stream);
            var document = XElement.Parse(reader.ReadToEnd());
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                document.Element(ns + "Application.Resources")!.Elements().Select(element => new XElement(element)));
            Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        }
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Flags)!.GetValue(o)!;
    private static object? Invoke(object o, string name, params object[] values) => o.GetType().GetMethod(name, Flags)!.Invoke(o, values);
    private static void Call(object o, string name, params object[] values)
    {
        if (Invoke(o, name, values) is Task operation) Await(operation);
        if (o is MainWindow && name != "DrainPlanetAsync") Await((Task)Invoke(o, "DrainPlanetAsync")!);
    }
    private static void PrepareWindow(MainWindow window)
    {
        Field<DispatcherTimer>(window, "timer").Stop();
        Await((Task)Invoke(window, "WaitForBackgroundAsync")!);
    }
    private static void CloseWindow(MainWindow window)
    {
        Await((Task)Invoke(window, "PrepareCloseAsync")!); window.Close();
    }
    private static void AddFocus(MainWindow window, int seconds)
    {
        Await((Task)Invoke(window, "WaitForBackgroundAsync")!);
        var store = Field<FocusStore>(window, "store"); var end = DateTimeOffset.Now;
        var id = store.BeginSession("合成专注", seconds, end.AddSeconds(-seconds));
        store.AddProgress(id, seconds, end); store.EndSession(id, "completed", end);
        var revision = window.GetType().GetField("focusDataRevision", Flags)!;
        revision.SetValue(window, (long)revision.GetValue(window)! + 1);
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            bool timedOut = false;
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(30) };
            timeout.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
            if (timedOut && !task.IsCompleted) throw new TimeoutException("Background operation did not finish within 30 seconds.");
        }
        task.GetAwaiter().GetResult();
    }
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }
    private static void Event(object o, string name) => Call(o, name, o, new RoutedEventArgs());
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Render(Window window, string path, int width = 1380, int height = 1040)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        var container = new Border { Background = window.Background, Child = content, Width = width, Height = height };
        container.Measure(new Size(width, height)); container.Arrange(new Rect(0, 0, width, height)); container.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(container);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var stream = File.Create(path); encoder.Save(stream);
        container.Child = null; window.Content = content;
        Console.WriteLine("Offline synthetic preview: " + Path.GetFullPath(path));
    }
}
