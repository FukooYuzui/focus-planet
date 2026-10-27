using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        var app = new App(); app.InitializeComponent();
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
        try
        {
            Run("new planet ignores historical focus and starts barren", () =>
            {
                var s = Field<PlanetSnapshot>(fresh, "planetSnapshot");
                Assert(s.TotalGrowth == 0 && s.Level == 0);
                Assert(Field<FrameworkElement>(fresh, "PlanetPage").Visibility == Visibility.Visible);
                Assert(fresh.Title.Contains("专注星球 0.5"));
            });
            Run("new focus feeds planet once and page navigation preserves timer setup", () =>
            {
                var store = Field<FocusStore>(fresh, "store"); var end = DateTimeOffset.Now;
                var id = store.BeginSession("验收新增专注", 120, end.AddMinutes(-2)); store.AddProgress(id, 120, end); store.EndSession(id, "completed", end);
                Call(fresh, "RefreshPlanet"); Assert(Field<PlanetSnapshot>(fresh, "planetSnapshot").TotalGrowth == 2);
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
            if (args.Length == 2 && args[0] == "--render") Render(fresh, Path.Combine(args[1], "专注星球-初生预览.png"));
        }
        finally { fresh.Close(); }

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
        SetDataDirectory(maturePath); var mature = new MainWindow();
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
                foreach (bool edit in new[] { false, true })
                {
                    var expanded = (Window)mature.GetType().GetMethod("CreatePlanetWorkspace", Flags)!.Invoke(mature, new object[] { edit })!;
                    Assert(host.Content == null && !expanded.IsVisible);
                    if (args.Length == 2 && args[0] == "--render") Render(expanded, Path.Combine(args[1], edit ? "专注星球-展开布置演示.png" : "专注星球-展开观景演示.png"), 1480, 960);
                    expanded.Close(); Assert(ReferenceEquals(host.Content, original) && ReferenceEquals(card.Parent, parent));
                }
            });
            Run("failed growth commit prevents travel and later retry credits original planet", () =>
            {
                var prefs = Field<object>(mature, "dashboardPreferences");
                prefs.GetType().GetProperty("CodexHome")!.SetValue(prefs, Path.Combine(root, "missing-source"));
                var focus = Field<FocusStore>(mature, "store"); var now = DateTimeOffset.Now;
                var id = focus.BeginSession("写入故障测试", 60, now.AddMinutes(-1)); focus.AddProgress(id, 60, now); focus.EndSession(id, "completed", now);
                using var db = new SqliteConnection("Data Source=" + Path.Combine(maturePath, "planet.db")); db.Open();
                using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER qa_fail_credit BEFORE INSERT ON planet_growth_credits BEGIN SELECT RAISE(ABORT, 'QA simulated write failure'); END;"; cmd.ExecuteNonQuery();
                bool travelled = false;
                var task = (Task)mature.GetType().GetMethod("TravelPlanetAsync", Flags)!.Invoke(mature, new object[] { (Action)(() => travelled = true) })!;
                task.GetAwaiter().GetResult(); Assert(!travelled);
                Assert(Field<PlanetGrowthStore>(mature, "planetStore").GetSnapshot(now).TotalGrowth == 9600);
                cmd.CommandText = "DROP TRIGGER qa_fail_credit"; cmd.ExecuteNonQuery();
                Call(mature, "RefreshPlanet"); var s = Field<PlanetSnapshot>(mature, "planetSnapshot"); Assert(s.PlanetId == planetId && s.TotalGrowth == 9601);
            });
            Call(mature, "PlanetTick");
            Call(mature, "Status", "离屏演示画面 · 使用独立合成数据，未改变真实进度");
            if (args.Length == 2 && args[0] == "--render") Render(mature, Path.Combine(args[1], "专注星球-成熟家园演示.png"));
            Field<CheckBox>(mature, "PlanetEditCheck").IsChecked = true; Event(mature, "TogglePlanetEdit");
            if (args.Length == 2 && args[0] == "--render") Render(mature, Path.Combine(args[1], "专注星球-布局演示.png"));
        }
        finally { mature.Close(); app.Shutdown(); }
        Console.WriteLine($"{passed} passed / {failed} failed; isolated fixtures: {root}");
        return failed == 0 ? 0 : 1;
    }
    private static void SetDataDirectory(string path) => typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, path);
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Flags)!.GetValue(o)!;
    private static void Call(object o, string name, params object[] values) => o.GetType().GetMethod(name, Flags)!.Invoke(o, values);
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
