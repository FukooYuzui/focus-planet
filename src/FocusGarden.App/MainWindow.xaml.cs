using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using FocusGarden.Core;
using FocusGarden.Timing;

namespace FocusGarden.App;
public partial class MainWindow : Window
{
    private readonly FocusStore store;
    private readonly LocalBridge bridge;
    private readonly FocusAppearance appearance;
    private FocusSettings settings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FocusClock? clock;
    private long? sessionId;
    private bool breaking, confirming, shuttingDown, hotkeyRegistered;
    private long lastTimestamp;
    private int plannedSeconds, year = DateTime.Now.Year;
    private ImmersionWindow? immersion;
    private HwndSource? hwndSource;
    private string currentTask = "";
    private DateTime displayedDate = DateTime.Today;
    private int nextHeatmapRefresh = 30;
    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width); MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        store = new FocusStore(Path.Combine(App.DataDirectory, "focus.db"));
        store.RecoverInterrupted(DateTimeOffset.Now);
        settings = store.LoadSettings();
        bridge = new LocalBridge(App.DataDirectory);
        appearance = new FocusAppearance(App.DataDirectory);
        LoadSettingsUi();
        ApplyAppearance(); UpdateWallClock();
        if (appearance.LoadWarning != null) Status(appearance.LoadWarning);
        if (App.TestMode) { TestButton.Visibility = Visibility.Visible; Title += " [独立验收模式]"; }
        timer.Tick += Tick; timer.Start();
        SourceInitialized += (_, _) =>
        {
            hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hwndSource.AddHook(WindowHook);
            hotkeyRegistered = NativeFocus.RegisterHotKey(hwndSource.Handle, 9081, 0x4003, 0x1B);
            if (!hotkeyRegistered) HotkeyHint.Text = "快捷键被占用；请使用可见的紧急退出按钮。";
        };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        Closing += ClosingWindow;
        Closed += (_, _) => { shuttingDown = true; timer.Stop(); SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged; if (hotkeyRegistered && hwndSource != null) NativeFocus.UnregisterHotKey(hwndSource.Handle, 9081); bridge.Dispose(); store.Dispose(); };
        RefreshStatistics();
    }
    private nint WindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == 9081) { Finish("emergency", "已紧急退出，应用限制已解除。"); handled = true; }
        return 0;
    }
    private void LoadSettingsUi()
    {
        FocusMinutesInput.Text = settings.FocusMinutes.ToString(); BreakMinutesInput.Text = settings.BreakMinutes.ToString(); ImmersiveCheck.IsChecked = settings.Immersive;
        AppsInput.Text = string.Join(Environment.NewLine, settings.BlockedApps); DomainsInput.Text = string.Join(Environment.NewLine, settings.BlockedDomains);
        TimerText.Text = FormatTime(settings.FocusMinutes * 60);
    }
    private void Page(string page)
    {
        FocusPage.Visibility = page == "focus" ? Visibility.Visible : Visibility.Collapsed;
        StatsPage.Visibility = page == "stats" ? Visibility.Visible : Visibility.Collapsed;
        RulesPage.Visibility = page == "rules" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "stats" => "每一份专注，都有迹可循。", "rules" => "把干扰，暂时放在门外。", _ => "把这一刻，留给重要的事。" };
        PageSubtitle.Text = page switch { "stats" => "积累看得见，也记得那些未完成的努力。", "rules" => "只在专注期间生效；紧急退出始终可用。", _ => "选一件小事，开始一段完整的专注。" };
    }
    private void ShowFocus(object sender, RoutedEventArgs e) { RefreshStatistics(); Page("focus"); }
    private void ShowStats(object sender, RoutedEventArgs e) { RefreshStatistics(); Page("stats"); }
    private void ShowRules(object sender, RoutedEventArgs e) { Page("rules"); RefreshAppList(); }
    private void Status(string text) => StatusText.Text = text;
    private void UpdateWallClock()
    {
        var now = DateTime.Now;
        CurrentTimeText.Text = "现在  " + now.ToString("HH:mm:ss");
        CurrentDateText.Text = now.ToString("yyyy 年 M 月 d 日  dddd", CultureInfo.GetCultureInfo("zh-CN"));
        if (now.Date != displayedDate) { displayedDate = now.Date; RefreshStatistics(); }
    }
    private void ApplyAppearance()
    {
        FocusHero.Background = appearance.BackgroundImage is { } image
            ? new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center }
            : (Brush)FindResource("Card");
        BackgroundHint.Text = appearance.ImageName is { } name ? "背景：" + name : "背景：纯色 · 主页与全屏同步";
        BackgroundHint.ToolTip = BackgroundHint.Text;
        ResetBackgroundButton.IsEnabled = appearance.BackgroundImage != null && clock == null;
    }
    private void ChooseBackground(object sender, RoutedEventArgs e)
    {
        if (clock != null) { Status("请在本轮结束后更换背景图片。"); return; }
        var dialog = new OpenFileDialog { Title = "选择专注背景图片", Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { appearance.SelectImage(dialog.FileName); ApplyAppearance(); Status("背景已保存，主页与全屏专注都会使用这张图片。"); }
        catch (Exception ex) { MessageBox.Show(this, "未能使用这张图片，原背景已保留。\n" + ex.Message, "选择背景图片", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void ResetBackground(object sender, RoutedEventArgs e)
    {
        if (clock != null) return;
        try { appearance.Reset(); ApplyAppearance(); Status("已恢复纯色背景，原始图片未改动。"); }
        catch (Exception ex) { Status("恢复纯色失败：" + ex.Message); }
    }
    private static string[] Lines(string text) => text.Split(['\r', '\n', ',', '，', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private bool SaveAllSettings()
    {
        try
        {
            if (!int.TryParse(FocusMinutesInput.Text, out var focus) || focus < 1 || focus > 180) throw new ArgumentException("专注时长请输入 1–180 分钟。");
            if (!int.TryParse(BreakMinutesInput.Text, out var rest) || rest < 1 || rest > 60) throw new ArgumentException("休息时长请输入 1–60 分钟。");
            var apps = Lines(AppsInput.Text).Select(AppRules.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (apps.Any(n => NativeFocus.Protected.Contains(n))) throw new ArgumentException("不能限制系统桌面、任务管理器或专注花园等逃生应用。");
            var candidate = new FocusSettings { FocusMinutes = focus, BreakMinutes = rest, Immersive = ImmersiveCheck.IsChecked == true, BlockedApps = apps, BlockedDomains = Lines(DomainsInput.Text).Select(DomainRules.Normalize).Distinct().ToArray() };
            store.SaveSettings(candidate); settings = candidate;
            AppsInput.Text = string.Join(Environment.NewLine, settings.BlockedApps); DomainsInput.Text = string.Join(Environment.NewLine, settings.BlockedDomains);
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "请检查设置", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
    }
    private void SaveRules(object sender, RoutedEventArgs e)
    {
        if (clock != null) { Status("专注或休息期间不能修改设置，请在结束后保存。"); return; }
        if (SaveAllSettings()) Status("设置已保存；下次专注时生效。");
    }
    private void StartFocus(object sender, RoutedEventArgs e) => Begin(false);
    private void StartTest(object sender, RoutedEventArgs e) { if (App.TestMode) Begin(true); }
    private void Begin(bool shortTest)
    {
        if (clock != null || !SaveAllSettings()) return;
        plannedSeconds = shortTest ? 10 : settings.FocusMinutes * 60;
        currentTask = string.IsNullOrWhiteSpace(TaskInput.Text) ? "一段专注时间" : TaskInput.Text.Trim();
        try { store.RecoverInterrupted(DateTimeOffset.Now); sessionId = store.BeginSession(currentTask, plannedSeconds, DateTimeOffset.Now); }
        catch (Exception ex) { Status("无法保存记录，未开启限制：" + ex.Message); return; }
        clock = new FocusClock(plannedSeconds); breaking = false; lastTimestamp = Stopwatch.GetTimestamp(); nextHeatmapRefresh = 30;
        SessionLabel.Text = shortTest ? "10 秒验收 · 使用独立测试记录" : "正在专注 · 一次只做一件事";
        SetActiveUi(true); UpdateTimer(); RefreshStatistics(); Page("focus");
        bridge.Publish(true, settings.BlockedDomains, clock.RemainingSeconds);
        Status(settings.BlockedDomains.Length > 0 ? "专注已开始；网站限制需要已连接的浏览器扩展。" : hotkeyRegistered ? "专注已开始；Ctrl + Alt + Esc 可立即退出。" : "专注已开始；紧急退出按钮始终可用（快捷键被占用）。");
        if (settings.Immersive) EnterFullscreen();
    }
    private void StartBreak(object sender, RoutedEventArgs e)
    {
        if (clock != null || !SaveAllSettings()) return;
        plannedSeconds = settings.BreakMinutes * 60; breaking = true; clock = new FocusClock(plannedSeconds); lastTimestamp = Stopwatch.GetTimestamp();
        SessionLabel.Text = "休息时间 · 起身走动，看看远处";
        SetActiveUi(true); FullscreenButton.Visibility = Visibility.Collapsed; UpdateTimer(); Status("休息不计入专注记录，也不启用使用限制。");
    }
    private void SetActiveUi(bool active)
    {
        StartButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        BreakButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        TestButton.Visibility = App.TestMode && !active ? Visibility.Visible : Visibility.Collapsed;
        EmergencyButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        EmergencyButton.Content = breaking ? "结束休息" : "紧急退出";
        FullscreenButton.Visibility = active && !breaking ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.IsEnabled = !active; TaskInput.IsEnabled = !active; AppsInput.IsEnabled = !active; DomainsInput.IsEnabled = !active;
        ChooseBackgroundButton.IsEnabled = !active; ResetBackgroundButton.IsEnabled = !active && appearance.BackgroundImage != null;
    }
    private void Tick(object? sender, EventArgs e)
    {
        UpdateWallClock();
        ApiStatus.Text = bridge.Error != null ? "本机连接服务不可用：" + bridge.Error : DateTimeOffset.UtcNow - bridge.LastContact < TimeSpan.FromSeconds(20) ? "● 浏览器扩展已连接（仅代表通信成功）" : "○ 等待扩展连接 · 本机服务已就绪";
        if (clock == null) return;
        var now = Stopwatch.GetTimestamp(); var delta = Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds; lastTimestamp = now;
        var progress = clock.Advance(delta);
        if (clock.GapDetected) { Finish("interrupted", "检测到休眠或长时间停顿，本轮已中断；停顿时间未计入。"); return; }
        if (progress > 0 && sessionId.HasValue)
        {
            try { store.AddProgress(sessionId.Value, progress, DateTimeOffset.Now); }
            catch (Exception ex) { ReleaseForError(); Status("记录写入失败，已解除限制。最后部分时长可能未保存：" + ex.Message); return; }
        }
        UpdateTimer();
        if (clock.ElapsedSeconds >= nextHeatmapRefresh) { nextHeatmapRefresh = clock.ElapsedSeconds + 30; RefreshStatistics(); }
        if (clock.IsComplete) { Finish("completed", breaking ? "休息结束，可以开始下一段专注了。" : "这一段完成了。休息一下，再出发。"); return; }
        if (!breaking)
        {
            bridge.Publish(true, settings.BlockedDomains, clock.RemainingSeconds);
            var blocked = NativeFocus.BlockForeground(settings.BlockedApps);
            if (blocked != null) { Status("已拦截 " + blocked + "，进程和文件仍保留。紧急退出可解除。"); if (immersion == null) { WindowState = WindowState.Normal; Activate(); } }
        }
    }
    private void UpdateTimer()
    {
        if (clock == null) return;
        TimerText.Text = FormatTime(clock.RemainingSeconds); TimerProgress.Value = (double)clock.ElapsedSeconds / plannedSeconds;
        immersion?.UpdateTime(TimerText.Text);
        Title = $"{TimerText.Text} · 专注花园 0.2" + (App.TestMode ? " [独立验收模式]" : "");
    }
    private static string FormatTime(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
    private void EnterFullscreenClick(object sender, RoutedEventArgs e) => EnterFullscreen();
    private void EnterFullscreen()
    {
        if (clock == null || breaking || immersion != null) return;
        immersion = new ImmersionWindow(currentTask, hotkeyRegistered, appearance.BackgroundImage, ConfirmEmergency) { Owner = this };
        immersion.UpdateTime(FormatTime(clock.RemainingSeconds)); immersion.Show();
    }
    private void EmergencyClick(object sender, RoutedEventArgs e) => ConfirmEmergency();
    private void ConfirmEmergency()
    {
        if (clock == null || confirming) return;
        if (breaking) { Finish("emergency", "休息已结束。"); return; }
        confirming = true;
        try
        {
            var answer = MessageBox.Show(immersion ?? (Window)this, "立即结束本轮并解除限制？\n已记录的专注时长会保留，状态记为“提前结束”。\n\n扩展在下次同步时解除；若浏览器挂起，可禁用扩展。", "紧急退出", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) Finish("emergency", "已提前结束，应用限制已解除；浏览器扩展将同步解除。");
        }
        finally { confirming = false; }
    }
    private void Finish(string status, string message)
    {
        if (clock == null) return;
        var id = sessionId;
        clock = null; sessionId = null; breaking = false;
        bridge.Publish(false, [], 0);
        immersion?.Release(); immersion = null; SetActiveUi(false);
        if (id.HasValue)
        {
            try { store.EndSession(id.Value, status, DateTimeOffset.Now); }
            catch (Exception ex) { message += " 记录结束状态保存失败，下次启动将恢复为中断：" + ex.Message; }
        }
        Title = "专注花园 0.2 · Focus Garden" + (App.TestMode ? " [独立验收模式]" : "");
        SessionLabel.Text = status == "completed" ? "完成一段，让专注慢慢生长。" : "下一次，继续从这里开始。";
        Status(message); RefreshStatistics(); Activate();
    }
    public void ReleaseForError()
    {
        var id = sessionId;
        clock = null; sessionId = null; breaking = false;
        bridge.Publish(false, [], 0); immersion?.Release(); immersion = null; SetActiveUi(false);
        if (id.HasValue) { try { store.EndSession(id.Value, "interrupted", DateTimeOffset.Now); } catch { /* Next start recovers the last durable checkpoint. */ } }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke(() => Finish("interrupted", "电脑进入休眠，本轮已中断并解除限制。")); }
    private void SessionChanged(object sender, SessionSwitchEventArgs e) { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff) Dispatcher.BeginInvoke(() => Finish("interrupted", "系统锁定或注销，本轮已中断。")); }
    private void ClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (clock != null && !shuttingDown) { e.Cancel = true; ConfirmEmergency(); }
    }
    private void RefreshApps(object sender, RoutedEventArgs e) => RefreshAppList();
    private void RefreshAppList()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p) { try { if (p.MainWindowHandle != 0 && !NativeFocus.Protected.Contains(p.ProcessName)) names.Add(p.ProcessName + ".exe"); } catch { } }
        }
        RunningApps.ItemsSource = names.Order().ToArray(); if (names.Count > 0) RunningApps.SelectedIndex = 0;
    }
    private void AddApp(object sender, RoutedEventArgs e)
    {
        if (clock != null) { Status("请在本轮结束后编辑限制。"); return; }
        if (RunningApps.SelectedItem is string name) AppsInput.Text = string.Join(Environment.NewLine, Lines(AppsInput.Text).Append(name).Distinct(StringComparer.OrdinalIgnoreCase));
    }
    private void CopyToken(object sender, RoutedEventArgs e) { Clipboard.SetText(bridge.Token); Status("配对码已复制，请粘贴到浏览器扩展中。配对码只供本机使用。"); }
    private void OpenExtensionFolder(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "browser-extension");
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Status("请打开交付包中的 browser-extension 文件夹；源码运行时位于项目根目录。");
    }
    private void PreviousYear(object sender, RoutedEventArgs e) { if (year > 2000) year--; RefreshStatistics(); }
    private void NextYear(object sender, RoutedEventArgs e) { if (year < DateTime.Now.Year) year++; RefreshStatistics(); }
    private void RefreshStatistics()
    {
        var daily = store.GetDailyTotals(year); var todayTotals = year == DateTime.Now.Year ? daily : store.GetDailyTotals(DateTime.Now.Year);
        todayTotals.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out var today);
        var sessions = store.GetSessions(100000);
        TodayStat.Text = $"{today / 60.0:0.#} 分钟";
        CompletedStat.Text = sessions.Count(s => s.Status == "completed" && s.EndedAt?.ToLocalTime().Year == year).ToString();
        DaysStat.Text = daily.Count(d => d.Value > 0) + " 天";
        YearLabel.Text = year.ToString(); YearSummary.Text = $"{year} 年累计 {daily.Values.Sum() / 3600.0:0.#} 小时 · 每一格是一天";
        StatsYearSummary.Text = $"当前统计年份：{year}。年度热力图和年份切换位于番茄钟主页。";
        DrawHeatmap(daily);
        HistoryPanel.Children.Clear();
        var latest = sessions.Take(12).ToArray();
        if (latest.Length == 0) HistoryPanel.Children.Add(new TextBlock { Text = "还没有记录。完成第一段专注，让花园开始生长。", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 18) });
        foreach (var s in latest)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            var left = new StackPanel(); left.Children.Add(new TextBlock { Text = s.Task, FontWeight = FontWeights.SemiBold }); left.Children.Add(new TextBlock { Text = s.StartedAt.ToLocalTime().ToString("MM-dd HH:mm"), FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 5, 0, 0) }); grid.Children.Add(left);
            var right = new TextBlock { Text = $"{s.ElapsedSeconds / 60.0:0.#} 分钟 · {StatusName(s.Status)}", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Green") }; Grid.SetColumn(right, 1); grid.Children.Add(right);
            HistoryPanel.Children.Add(new Border { Child = grid, BorderBrush = new SolidColorBrush(Color.FromRgb(40, 59, 64)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 0, 12) });
        }
    }
    private static string StatusName(string s) => s switch { "completed" => "已完成", "emergency" => "提前结束", "interrupted" => "中断", _ => "进行中" };
    private void DrawHeatmap(Dictionary<string, int> daily)
    {
        Heatmap.Children.Clear(); Heatmap.RowDefinitions.Clear(); Heatmap.ColumnDefinitions.Clear();
        var first = new DateTime(year, 1, 1); var offset = (int)first.DayOfWeek; var days = DateTime.IsLeapYear(year) ? 366 : 365; var weeks = (offset + days + 6) / 7;
        Heatmap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        for (int c = 0; c < weeks; c++) Heatmap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        Heatmap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        for (int r = 0; r < 7; r++) Heatmap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        foreach (var day in new[] { 1, 3, 5 }) { var t = new TextBlock { Text = new[] { "日", "一", "二", "三", "四", "五", "六" }[day], FontSize = 10, Foreground = (Brush)FindResource("Muted") }; Grid.SetRow(t, day + 1); Heatmap.Children.Add(t); }
        var colors = new[] { "#26383E", "#204E3B", "#357450", "#62AB74", "#A6E3BA" };
        for (int d = 0; d < days; d++)
        {
            var date = first.AddDays(d); var col = (offset + d) / 7 + 1; var row = (offset + d) % 7 + 1;
            daily.TryGetValue(date.ToString("yyyy-MM-dd"), out var seconds); int level = seconds == 0 ? 0 : seconds < 1500 ? 1 : seconds < 3000 ? 2 : seconds < 6000 ? 3 : 4;
            var box = new Border { Width = 10, Height = 14, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[level])), ToolTip = $"{date:yyyy-MM-dd} · {seconds / 60.0:0.##} 分钟", Opacity = date > DateTime.Today ? .35 : 1 };
            Grid.SetColumn(box, col); Grid.SetRow(box, row); Heatmap.Children.Add(box);
            if (date.Day == 1) { var label = new TextBlock { Text = date.Month + "月", FontSize = 10, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.NoWrap }; Grid.SetColumn(label, col); Grid.SetColumnSpan(label, Math.Min(3, weeks - col + 1)); Heatmap.Children.Add(label); }
        }
    }
    private void ExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "专注记录-" + DateTime.Now.ToString("yyyyMMdd") + ".csv", Filter = "CSV 文件|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Cell(string value) { if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        var rows = new List<string> { "编号,任务,开始时间,结束时间,计划秒数,实际秒数,状态" };
        rows.AddRange(store.GetSessions(100000).Select(s => string.Join(",", new[] { s.Id.ToString(), Cell(s.Task), Cell(s.StartedAt.ToString("O")), Cell(s.EndedAt?.ToString("O") ?? ""), s.PlannedSeconds.ToString(), s.ElapsedSeconds.ToString(), Cell(StatusName(s.Status)) })));
        File.WriteAllLines(dialog.FileName, rows, new UTF8Encoding(true)); Status("已导出记录到：" + dialog.FileName);
    }
}
