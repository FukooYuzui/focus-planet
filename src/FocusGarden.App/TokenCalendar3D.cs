using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

/// <summary>A local, interactive 3D calendar. Missing records deliberately remain distinct from recorded zero.</summary>
internal sealed class TokenCalendar3D : UserControl
{
    private readonly Viewport3D viewport = new() { ClipToBounds = true, Focusable = true };
    private readonly ModelVisual3D scene = new();
    private readonly PerspectiveCamera camera = new() { FieldOfView = 45, NearPlaneDistance = .1, FarPlaneDistance = 1000 };
    private readonly Dictionary<GeometryModel3D, TokenCalendarDay> hitDays = new();
    private readonly TextBlock details = new() { Text = "拖动旋转 · 滚轮缩放 · 悬停查看单日用量 · 双击重置", Foreground = Brush("#BBCAC8"), FontSize = 11, Margin = new Thickness(12, 8, 12, 10), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock peakLabel = new() { Foreground = Brush("#BBCAC8"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox scaleSelector;
    private readonly ToolTip dayTip = new() { Placement = PlacementMode.MousePoint, StaysOpen = true, Background = Brush("#182B29"), Foreground = Brushes.White, BorderBrush = Brush("#5B8C74"), Padding = new Thickness(10) };
    private Dictionary<string, long> data = new(StringComparer.Ordinal);
    private int currentYear = DateTime.Today.Year;
    private DateTime dataToday = DateTime.MinValue;
    private bool squareRoot = true;
    private double yaw = -28, pitch = 53, zoom = 1;
    private Point lastPointer;
    private bool dragging;
    public string? SelectedDay { get; private set; }
    public long? SelectedTokens { get; private set; }
    internal int SceneBuildCount { get; private set; }
    internal int RenderedDayCount => hitDays.Count;

    public TokenCalendar3D()
    {
        Background = Brush("#0C1516"); Foreground = Brushes.White;
        MinHeight = 340;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new WrapPanel { Margin = new Thickness(12, 10, 12, 2), VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(new TextBlock { Text = "柱高", Foreground = Brush("#DCE9E3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
        scaleSelector = new ComboBox { Width = 100, Foreground = Brushes.Black, Background = Brushes.White, FontSize = 12, ItemsSource = new[] { "平方根", "线性" }, SelectedIndex = 0, VerticalContentAlignment = VerticalAlignment.Center, Height = 28, ToolTip = "平方根刻度让较小的每日用量也容易看见；线性高度与用量成正比。" };
        scaleSelector.SelectionChanged += (_, _) => { bool next = scaleSelector.SelectedIndex == 0; if (next == squareRoot) return; squareRoot = next; BuildScene(); };
        toolbar.Children.Add(scaleSelector);
        var reset = new Button { Content = "重置视角", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(10, 0, 0, 0), FontSize = 12, Foreground = Brush("#F1F7F3"), Background = Brush("#2B403D"), BorderThickness = new Thickness(0) };
        reset.Click += (_, _) => ResetView(); toolbar.Children.Add(reset); toolbar.Children.Add(peakLabel);
        layout.Children.Add(toolbar);
        var surface = new Border { Background = Brush("#0C1516"), Child = viewport, ClipToBounds = true, Margin = new Thickness(0, 3, 0, 0) };
        Grid.SetRow(surface, 1); layout.Children.Add(surface);
        Grid.SetRow(details, 2); layout.Children.Add(details); Content = layout;
        viewport.Camera = camera; viewport.Children.Add(scene); dayTip.PlacementTarget = viewport;
        viewport.SizeChanged += (_, _) => UpdateCamera();
        surface.MouseLeftButtonDown += BeginDrag; surface.MouseMove += PointerMoved; surface.MouseLeftButtonUp += EndDrag;
        viewport.LostMouseCapture += (_, _) => { dragging = false; Cursor = Cursors.Arrow; };
        surface.MouseLeave += (_, _) => dayTip.IsOpen = false;
        surface.PreviewMouseWheel += (_, e) => { zoom = Math.Clamp(zoom * Math.Pow(.86, e.Delta / 120.0), .40, 2.2); dayTip.IsOpen = false; UpdateCamera(); e.Handled = true; };
        viewport.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Left: yaw -= 8; break;
                case Key.Right: yaw += 8; break;
                case Key.Up: pitch = Math.Min(82, pitch + 6); break;
                case Key.Down: pitch = Math.Max(20, pitch - 6); break;
                case Key.Home: ResetView(); e.Handled = true; return;
                case Key.Add: case Key.OemPlus: zoom = Math.Max(.40, zoom * .86); break;
                case Key.Subtract: case Key.OemMinus: zoom = Math.Min(2.2, zoom / .86); break;
                default: return;
            }
            UpdateCamera(); e.Handled = true;
        };
        Unloaded += (_, _) => { dayTip.IsOpen = false; if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture(); };
        SetData(currentYear, new Dictionary<string, long>());
    }

    public void SetData(int year, IReadOnlyDictionary<string, long> dailyTotals)
    {
        Dispatcher.VerifyAccess();
        // Validate even on a cache hit, and take a snapshot so callers can mutate their dictionaries safely.
        var days = TokenCalendar3DGeometry.BuildCalendar(year, dailyTotals, DateTime.Today, squareRoot);
        var snapshot = days.Where(d => d.Tokens.HasValue).ToDictionary(d => d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Tokens!.Value, StringComparer.Ordinal);
        if (SceneBuildCount > 0 && year == currentYear && dataToday == DateTime.Today && data.Count == snapshot.Count && data.All(p => snapshot.TryGetValue(p.Key, out var n) && n == p.Value)) return;
        currentYear = year; data = snapshot; dataToday = DateTime.Today;
        BuildScene(days);
    }

    private void BuildScene(IReadOnlyList<TokenCalendarDay>? days = null)
    {
        days ??= TokenCalendar3DGeometry.BuildCalendar(currentYear, data, DateTime.Today, squareRoot);
        var model = new Model3DGroup();
        model.Children.Add(new AmbientLight(Color.FromRgb(105, 117, 110)));
        model.Children.Add(new DirectionalLight(Color.FromRgb(245, 255, 247), new Vector3D(-.5, -1, -.45)));
        model.Children.Add(new DirectionalLight(Color.FromRgb(105, 139, 120), new Vector3D(1, -.35, .5)));
        hitDays.Clear(); SelectedDay = null; SelectedTokens = null; dayTip.IsOpen = false;
        AddBox(model, -.25, -.42, -.25, TokenCalendar3DGeometry.SceneWidth + .5, .30, TokenCalendar3DGeometry.SceneDepth + .5, Material("#47504F"));
        for (int month = 1; month <= 12; month++)
        {
            double x = (month - 1) % 4 * TokenCalendar3DGeometry.MonthPitchX;
            double z = (month - 1) / 4 * TokenCalendar3DGeometry.MonthPitchZ;
            AddBox(model, x, -.10, z, TokenCalendar3DGeometry.MonthWidth, .27, TokenCalendar3DGeometry.MonthDepth, Material("#697571"));
            var label = MonthLabel(currentYear, month);
            model.Children.Add(new GeometryModel3D(TokenCalendar3DGeometry.LabelPlane(x + .38, .18, z + .12, 8.0, 1.40), new DiffuseMaterial(label)));
        }
        long maximum = data.Count == 0 ? 0 : data.Values.Max();
        foreach (var day in days)
        {
            string color;
            if (day.IsFuture) color = day.Tokens.HasValue ? "#234B37" : "#39433F";
            else if (!day.Tokens.HasValue) color = "#515E58";
            else if (day.Tokens == 0) color = "#437858";
            else
            {
                double fraction = maximum == 0 ? 0 : Math.Sqrt((double)day.Tokens.Value / maximum);
                color = $"#{(int)(24 + 21 * fraction):X2}{(int)(128 + 77 * fraction):X2}{(int)(63 + 52 * fraction):X2}";
            }
            var bar = AddBox(model, day.X, .18, day.Z, 1.00, day.Height, .82, Material(color));
            hitDays.Add(bar, day);
        }
        model.Freeze(); scene.Content = model; SceneBuildCount++;
        peakLabel.Text = maximum > 0 ? $"{currentYear} · 单日最高 {maximum:N0} Tokens" : $"{currentYear} · 暂无正值记录";
        details.Text = "拖动旋转 · 滚轮缩放 · 悬停查看单日用量 · 灰格表示没有本机记录";
        UpdateCamera();
    }

    private static GeometryModel3D AddBox(Model3DGroup group, double x, double y, double z, double width, double height, double depth, Material material)
    {
        var result = new GeometryModel3D(TokenCalendar3DGeometry.Box(x, y, z, width, height, depth), material); group.Children.Add(result); return result;
    }

    private static ImageBrush MonthLabel(int year, int month)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brush("#697571"), null, new Rect(0, 0, 640, 112));
            var title = new FormattedText($"{month:00}  /  {year}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 38, Brush("#101B16"), 1);
            drawing.DrawText(title, new Point(4, 0));
            var weekdays = new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
            for (int i = 0; i < 7; i++)
            {
                var text = new FormattedText(weekdays[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 19, Brush("#18281F"), 1);
                drawing.DrawText(text, new Point(9 + i * 89.5, 76));
            }
        }
        var bitmap = new RenderTargetBitmap(640, 112, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill }; brush.Freeze(); return brush;
    }

    private void ResetView() { yaw = -28; pitch = 53; zoom = 1; dayTip.IsOpen = false; UpdateCamera(); }
    private void UpdateCamera()
    {
        double radians = Math.PI / 180, y = yaw * radians, p = pitch * radians;
        double aspect = Math.Max(.5, viewport.ActualWidth / Math.Max(1, viewport.ActualHeight));
        var target = new Point3D(TokenCalendar3DGeometry.SceneWidth / 2, 1.0, TokenCalendar3DGeometry.SceneDepth / 2);
        double halfX = TokenCalendar3DGeometry.SceneWidth / 2 + .5, halfZ = TokenCalendar3DGeometry.SceneDepth / 2 + .5, halfY = 2.8;
        double halfHorizontal = Math.Abs(Math.Cos(y)) * halfX + Math.Abs(Math.Sin(y)) * halfZ;
        double halfVertical = Math.Abs(Math.Sin(y) * Math.Sin(p)) * halfX + Math.Abs(Math.Cos(y) * Math.Sin(p)) * halfZ + Math.Cos(p) * halfY;
        double depth = Math.Abs(Math.Sin(y) * Math.Cos(p)) * halfX + Math.Abs(Math.Cos(y) * Math.Cos(p)) * halfZ + Math.Sin(p) * halfY;
        double tangent = Math.Tan(camera.FieldOfView / 2 * radians);
        double distance = (Math.Max(halfHorizontal / tangent, halfVertical * aspect / tangent) + depth * .5) * 1.08 * zoom;
        var offset = new Vector3D(Math.Sin(y) * Math.Cos(p), Math.Sin(p), Math.Cos(y) * Math.Cos(p)) * distance;
        camera.Position = target + offset; camera.LookDirection = -offset; camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        viewport.Focus();
        if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
        dragging = true; lastPointer = e.GetPosition(viewport); viewport.CaptureMouse(); Cursor = Cursors.SizeAll; dayTip.IsOpen = false; e.Handled = true;
    }
    private void EndDrag(object sender, MouseButtonEventArgs e) { if (!dragging) return; dragging = false; viewport.ReleaseMouseCapture(); Cursor = Cursors.Arrow; e.Handled = true; }
    private void PointerMoved(object sender, MouseEventArgs e)
    {
        var pointer = e.GetPosition(viewport);
        if (dragging)
        {
            yaw -= (pointer.X - lastPointer.X) * .38; pitch = Math.Clamp(pitch + (pointer.Y - lastPointer.Y) * .26, 20, 82); lastPointer = pointer;
            UpdateCamera(); e.Handled = true; return;
        }
        TokenCalendarDay? found = null;
        VisualTreeHelper.HitTest(viewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult ray && ray.ModelHit is GeometryModel3D geometry && hitDays.TryGetValue(geometry, out var day)) { found = day; return HitTestResultBehavior.Stop; }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(pointer));
        if (found == null) { dayTip.IsOpen = false; return; }
        SelectedDay = found.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); SelectedTokens = found.Tokens;
        var text = $"{SelectedDay}  {found.Date.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"))}\n" + (found.Tokens.HasValue ? $"{found.Tokens.Value:N0} Tokens" : "没有本机记录（不代表实际用量为 0）") + (found.IsFuture ? "\n未来日期" : "");
        details.Text = text.Replace('\n', ' '); dayTip.Content = text; dayTip.IsOpen = true;
    }

    private static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Material Material(string color) { var material = new DiffuseMaterial(Brush(color)); material.Freeze(); return material; }
}
