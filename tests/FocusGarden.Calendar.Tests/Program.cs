using System.Windows.Media.Media3D;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text.Json;
using System.Globalization;
using FocusGarden.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int passed = 0, failed = 0;
        void Run(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; } }
        IReadOnlyList<TokenCalendarDay> Days(int year, Dictionary<string, long>? values = null, bool sqrt = true) => TokenCalendar3DGeometry.BuildCalendar(year, values ?? [], new DateTime(2026, 10, 27), sqrt);
        Run("leap and nonleap calendars contain exactly real dates", () => { Assert(Days(2024).Count == 366 && Days(2026).Count == 365); Assert(Days(2024).Single(d => d.Date.Month == 2 && d.Date.Day == 29) != null); Assert(Days(2026).Select(d => d.Date).Distinct().Count() == 365); });
        Run("twelve month tiles have four columns and three rows", () => { var days = Days(2026); Assert(days.GroupBy(d => d.Date.Month).Count() == 12); foreach (var d in days) Assert(d.MonthColumn == (d.Date.Month - 1) % 4 && d.MonthRow == (d.Date.Month - 1) / 4); Assert(days.Max(d => d.MonthColumn) == 3 && days.Max(d => d.MonthRow) == 2); });
        Run("week coordinates match calendar weekdays including six-week months", () => { foreach (var d in Days(2026)) { Assert(d.Weekday == (int)d.Date.DayOfWeek && d.Week is >= 0 and <= 5); Assert(d.Week == ((int)new DateTime(2026, d.Date.Month, 1).DayOfWeek + d.Date.Day - 1) / 7); } Assert(Days(2026).Any(d => d.Week == 5)); });
        Run("missing records differ from recorded zero and future dates", () => { var days = Days(2026, new() { ["2026-01-01"] = 0 }); Assert(days[0].Tokens == 0 && days[1].Tokens == null); Assert(days.Single(d => d.Date == new DateTime(2026, 10, 27)).IsFuture == false); Assert(days.Last().IsFuture); });
        Run("heights are finite bounded monotonic and square root distinguishes small usage", () => { var values = new Dictionary<string, long> { ["2026-01-01"] = 1, ["2026-01-02"] = 10, ["2026-01-03"] = 100, ["2026-01-04"] = long.MaxValue }; var sqrt = Days(2026, values); var linear = Days(2026, values, false); Assert(sqrt.All(d => double.IsFinite(d.Height) && d.Height is >= .08 and <= 4.401)); Assert(sqrt[0].Height <= sqrt[1].Height && sqrt[1].Height <= sqrt[2].Height && sqrt[2].Height <= sqrt[3].Height); Assert(sqrt[1].Height > linear[1].Height); });
        Run("negative counts and malformed dates are rejected", () => { Throws(() => Days(2026, new() { ["2026-01-01"] = -1 })); Throws(() => Days(2026, new() { ["not-a-date"] = 1 })); Throws(() => Days(0)); });
        Run("other years do not change selected-year heights", () => { var a = Days(2026, new() { ["2026-02-01"] = 50 }); var b = Days(2026, new() { ["2026-02-01"] = 50, ["2025-01-01"] = long.MaxValue }); Assert(a.SequenceEqual(b)); });
        Run("bar geometry has outward flat normals and no degenerate triangles", () => { var mesh = TokenCalendar3DGeometry.Box(1, 2, 3, 1, 4, .8); Assert(mesh.IsFrozen && mesh.Positions.Count == 24 && mesh.TriangleIndices.Count == 36); for (int i = 0; i < mesh.TriangleIndices.Count; i += 3) { var a = mesh.TriangleIndices[i]; var b = mesh.TriangleIndices[i + 1]; var c = mesh.TriangleIndices[i + 2]; var cross = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]); Assert(cross.Length > 0 && Vector3D.DotProduct(cross, mesh.Normals[a]) > 0); } Throws(() => TokenCalendar3DGeometry.Box(0, 0, 0, 0, 1, 1)); });
        Run("control snapshots input and reuses identical scene", () => { var control = new TokenCalendar3D(); var values = new Dictionary<string, long> { ["2024-02-29"] = 100 }; control.SetData(2024, values); var before = control.SceneBuildCount; Assert(control.RenderedDayCount == 366); control.SetData(2024, new Dictionary<string, long>(values)); Assert(control.SceneBuildCount == before); values["2024-03-01"] = 200; control.SetData(2024, values); Assert(control.SceneBuildCount == before + 1 && control.RenderedDayCount == 366); });
        Run("deferred calendar builds only the first requested data scene", () => { var control = new TokenCalendar3D(deferInitialScene: true); Assert(control.SceneBuildCount == 0 && control.RenderedDayCount == 0); var values = new Dictionary<string, long> { ["2024-02-29"] = 100 }; control.SetData(2024, values); Assert(control.SceneBuildCount == 1 && control.RenderedDayCount == 366); control.SetData(2024, values); Assert(control.SceneBuildCount == 1); });
        Console.WriteLine($"{passed} passed / {failed} failed");
        if (args.Length == 2 && args[0] == "--render")
        {
            // Synthetic QA fixture only; production UI receives actual local records from its caller.
            var fixture = new Dictionary<string, long>();
            for (int month = 1; month <= 9; month++) for (int day = 1; day <= DateTime.DaysInMonth(2026, month); day++) if ((day + month) % 5 != 0) fixture[$"2026-{month:00}-{day:00}"] = (long)(day * month * 10000 + (day % 8 == 0 ? 1200000 : 0));
            var control = new TokenCalendar3D { Width = 1100, Height = 650 }; control.SetData(2026, fixture);
            control.Measure(new Size(1100, 650)); control.Arrange(new Rect(0, 0, 1100, 650)); control.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); using var file = File.Create(output); encoder.Save(file);
            Console.WriteLine("Synthetic QA preview: " + output);
        }
        else if (args.Length == 4 && args[0] == "--render-data")
        {
            if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int year) || year is < 1 or > 9999)
                throw new ArgumentException("Year must be between 1 and 9999.");
            var values = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Path.GetFullPath(args[1])))
                ?? throw new InvalidDataException("Expected a JSON object with yyyy-MM-dd keys and integer token counts.");
            var control = new TokenCalendar3D(); control.SetData(year, values);
            var current = values.Where(p => p.Key.StartsWith(year.ToString("0000", CultureInfo.InvariantCulture) + "-", StringComparison.Ordinal)).ToArray();
            decimal total = current.Sum(p => (decimal)p.Value);
            const int width = 1600, height = 1080;
            var layout = new Grid { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(12, 21, 22)) };
            layout.SetValue(TextElement.FontFamilyProperty, new FontFamily("Microsoft YaHei UI"));
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel { Margin = new Thickness(34, 26, 34, 12) };
            heading.Children.Add(new TextBlock { Text = "Codex Token 3D 日历", FontSize = 38, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            heading.Children.Add(new TextBlock { Text = "本机日志估算（含缓存输入，非账单）", FontSize = 18, Foreground = new SolidColorBrush(Color.FromRgb(174, 210, 190)), Margin = new Thickness(0, 8, 0, 0) });
            heading.Children.Add(new TextBlock { Text = $"{year} 年 · {current.Length} 天有记录 · 累计 {total:N0} Tokens", FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(188, 202, 198)), Margin = new Thickness(0, 8, 0, 0) });
            layout.Children.Add(heading);
            control.Margin = new Thickness(22, 0, 22, 0); Grid.SetRow(control, 1); layout.Children.Add(control);
            var note = new TextBlock { Text = "仅覆盖已发现的本机日志；灰色格表示没有记录，不代表实际用量为零。", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(188, 202, 198)), Margin = new Thickness(34, 8, 34, 22) };
            Grid.SetRow(note, 2); layout.Children.Add(note);
            layout.Measure(new Size(width, height)); layout.Arrange(new Rect(0, 0, width, height)); layout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); using var file = File.Create(output); encoder.Save(file);
            Console.WriteLine($"Real local-data preview: {output}; year={year}; recordedDays={current.Length}; tokens={total:0}");
        }
        return failed == 0 ? 0 : 1;
    }
    private static void Assert(bool test) { if (!test) throw new Exception("Assertion failed"); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected argument rejection"); }
}
