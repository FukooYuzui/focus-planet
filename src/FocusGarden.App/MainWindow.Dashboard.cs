using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FocusGarden.Core;
using Microsoft.Win32;

namespace FocusGarden.App;
public partial class MainWindow
{
    private DashboardPreferences dashboardPreferences = null!;
    private AppUsageStore usageStore = null!;
    private AppUsageTracker usageTracker = null!;
    private CodexUsageImporter tokenImporter = null!;
    private TokenCalendar3D tokenCalendar = null!;
    private DateTime usageDate = DateTime.Today;
    private int tokenYear = DateTime.Today.Year;
    private long dashboardTick;
    private long lastTokenImportTick = Stopwatch.GetTimestamp();
    private bool importingTokens, dashboardReady, dashboardClosed;
    private bool powerSuspended, desktopLocked;
    private CancellationTokenSource? importCancellation;

    private void InitializeDashboard()
    {
        dashboardPreferences = DashboardPreferences.Load(App.DataDirectory);
        usageStore = new AppUsageStore(Path.Combine(App.DataDirectory, "focus.db"));
        usageTracker = new AppUsageTracker(usageStore) { Enabled = dashboardPreferences.TrackApplications };
        tokenImporter = new CodexUsageImporter(Path.Combine(App.DataDirectory, "codex-usage.db"));
        tokenCalendar = new TokenCalendar3D(); TokenCalendarHost.Content = tokenCalendar;
        UsageEnabledCheck.IsChecked = dashboardPreferences.TrackApplications;
        AutoRefreshTokensCheck.IsChecked = dashboardPreferences.AutoRefreshTokens;
        dashboardReady = true; dashboardTick = Stopwatch.GetTimestamp();
        RefreshUsageUi(); RefreshTokenUi();
        TokenStatusText.Text = "启动时将读取本机用量；也可手动刷新。首次读取历史日志可能需要一会儿。";
        CodexSourceText.Text = "当前图表来源：" + (tokenImporter.ActiveSource ?? "尚无缓存") + "\n读取目录：" + dashboardPreferences.ResolveCodexHome();
        Loaded += async (_, _) => await ImportTokensAsync();
    }
    private void DashboardTick()
    {
        if (!dashboardReady || dashboardClosed) return;
        try { usageTracker.Sample(); }
        catch (Exception ex) { usageTracker.Enabled = false; UsageEnabledCheck.IsChecked = false; UsageStatusText.Text = "应用统计已暂停：" + ex.Message; }
        if (Stopwatch.GetElapsedTime(dashboardTick).TotalSeconds >= 5)
        {
            dashboardTick = Stopwatch.GetTimestamp(); RefreshUsageUi();
        }
        if (dashboardPreferences.AutoRefreshTokens && !importingTokens && Stopwatch.GetElapsedTime(lastTokenImportTick).TotalSeconds >= 120) _ = ImportTokensAsync();
    }
    private void CloseDashboard()
    {
        dashboardClosed = true;
        if (!dashboardReady) return;
        importCancellation?.Cancel();
        usageTracker.Flush(); usageStore.Dispose();
        if (!importingTokens) tokenImporter.Dispose();
    }
    private void JumpToUsage(object sender, RoutedEventArgs e)
    {
        Page("focus"); RefreshUsageUi();
        Dispatcher.BeginInvoke(() => UsageCard.BringIntoView(), DispatcherPriority.Loaded);
    }
    private void JumpToTokens(object sender, RoutedEventArgs e)
    {
        Page("focus");
        Dispatcher.BeginInvoke(() => TokenCard.BringIntoView(), DispatcherPriority.Loaded);
    }
    private void ToggleUsage(object sender, RoutedEventArgs e)
    {
        if (!dashboardReady) return;
        dashboardPreferences.TrackApplications = UsageEnabledCheck.IsChecked == true;
        usageTracker.Enabled = dashboardPreferences.TrackApplications;
        try { dashboardPreferences.Save(App.DataDirectory); }
        catch (Exception ex) { Status("记录开关已生效，但未能保存设置：" + ex.Message); }
        RefreshUsageUi();
    }
    private void PreviousUsageDay(object sender, RoutedEventArgs e) { if (usageDate.Year > 2000) usageDate = usageDate.AddDays(-1); RefreshUsageUi(); }
    private void NextUsageDay(object sender, RoutedEventArgs e) { if (usageDate < DateTime.Today) usageDate = usageDate.AddDays(1); RefreshUsageUi(); }
    private void UsageToday(object sender, RoutedEventArgs e) { usageDate = DateTime.Today; RefreshUsageUi(); }
    private static string Duration(int seconds) => seconds >= 3600 ? $"{seconds / 3600} 小时 {seconds % 3600 / 60} 分" : seconds >= 60 ? $"{seconds / 60} 分 {seconds % 60} 秒" : $"{seconds} 秒";
    private static string AppDisplayName(string process) => process.ToLowerInvariant() switch
    {
        "msedge.exe" => "Microsoft Edge", "chrome.exe" => "Google Chrome", "firefox.exe" => "Firefox", "codex.exe" => "Codex", "chatgpt.exe" => "ChatGPT", "focusgarden.exe" or "专注星球.exe" or "focusplanet.exe" => "专注星球", "code.exe" => "Visual Studio Code", "obsidian.exe" => "Obsidian", "winword.exe" => "Word", "powerpnt.exe" => "PowerPoint", "excel.exe" => "Excel", "wechat.exe" or "weixin.exe" => "微信", "qq.exe" => "QQ", "explorer.exe" => "文件资源管理器", "notepad.exe" => "记事本", _ => process
    };
    private void RefreshUsageUi()
    {
        if (!dashboardReady) return;
        UsageDateLabel.Text = usageDate.ToString("yyyy-MM-dd") + (usageDate == DateTime.Today ? " · 今天" : "");
        UsageRows.Children.Clear();
        try
        {
            var rows = usageTracker.GetDailyUsage(usageDate).OrderByDescending(x => x.Seconds).ToArray();
            var total = rows.Sum(x => x.Seconds);
            UsageSummary.Text = "已记录 " + Duration(total) + " · " + rows.Length + " 个应用";
            if (rows.Length == 0) UsageRows.Children.Add(new TextBlock { Text = "这一天还没有应用使用记录。记录从本程序运行后开始，不补算此前时长。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 10, 0, 8) });
            foreach (var row in rows)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 7, 0, 4) };
                var line = new DockPanel();
                var time = new TextBlock { Text = Duration(row.Seconds), Foreground = (Brush)FindResource("Green"), FontSize = 12, Margin = new Thickness(14, 0, 0, 0) }; DockPanel.SetDock(time, Dock.Right); line.Children.Add(time);
                line.Children.Add(new TextBlock { Text = AppDisplayName(row.AppName), ToolTip = row.AppName, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                panel.Children.Add(line);
                panel.Children.Add(new ProgressBar { Value = row.Seconds, Maximum = Math.Max(total, 1), Height = 3, Margin = new Thickness(0, 6, 0, 0), Background = new SolidColorBrush(Color.FromRgb(39, 59, 65)), Foreground = (Brush)FindResource("Green"), BorderThickness = new Thickness(0) });
                UsageRows.Children.Add(panel);
            }
            UsageStatusText.Text = usageTracker.LastError ?? usageTracker.StateText;
        }
        catch (Exception ex) { UsageStatusText.Text = "应用时长读取失败：" + ex.Message; }
    }
    private static string TokenNumber(long value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000.0:0.##}B" : value >= 1_000_000 ? $"{value / 1_000_000.0:0.##}M" : value >= 1_000 ? $"{value / 1_000.0:0.##}K" : value.ToString("N0");
    private void RefreshTokenUi()
    {
        if (!dashboardReady || dashboardClosed) return;
        var daily = tokenImporter.GetDailyTotals(tokenYear);
        var today = tokenYear == DateTime.Today.Year ? daily : tokenImporter.GetDailyTotals(DateTime.Today.Year);
        today.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out var todayTokens);
        long yearTotal = daily.Values.Sum();
        TokenYearLabel.Text = tokenYear.ToString();
        TokenYearTotal.Text = daily.Count == 0 ? "暂无记录" : TokenNumber(yearTotal); TokenYearTotal.ToolTip = yearTotal.ToString("N0") + " tokens";
        TokenTodayTotal.Text = today.ContainsKey(DateTime.Today.ToString("yyyy-MM-dd")) ? TokenNumber(todayTokens) : "暂无记录"; TokenTodayTotal.ToolTip = todayTokens.ToString("N0") + " tokens";
        TokenDays.Text = daily.Count.ToString(); tokenCalendar.SetData(tokenYear, daily);
    }
    private void PreviousTokenYear(object sender, RoutedEventArgs e) { if (tokenYear > 2000 && !importingTokens) { tokenYear--; RefreshTokenUi(); } }
    private void NextTokenYear(object sender, RoutedEventArgs e) { if (tokenYear < DateTime.Today.Year && !importingTokens) { tokenYear++; RefreshTokenUi(); } }
    private async void RefreshTokensClick(object sender, RoutedEventArgs e) => await ImportTokensAsync();
    private void ToggleTokenRefresh(object sender, RoutedEventArgs e)
    {
        dashboardPreferences.AutoRefreshTokens = AutoRefreshTokensCheck.IsChecked == true;
        lastTokenImportTick = Stopwatch.GetTimestamp();
        try { dashboardPreferences.Save(App.DataDirectory); }
        catch (Exception ex) { Status("刷新开关已生效，但未能保存设置：" + ex.Message); }
    }
    private async void ChooseCodexFolder(object sender, RoutedEventArgs e)
    {
        if (importingTokens) return;
        var dialog = new OpenFolderDialog { Title = "选择包含 sessions 或 archived_sessions 的 Codex 数据目录" };
        if (dialog.ShowDialog(this) != true) return;
        if (!Directory.Exists(Path.Combine(dialog.FolderName, "sessions")) && !Directory.Exists(Path.Combine(dialog.FolderName, "archived_sessions"))) { Status("所选目录不含 sessions 或 archived_sessions，请选择 Codex 数据根目录。"); return; }
        dashboardPreferences.CodexHome = dialog.FolderName;
        try { dashboardPreferences.Save(App.DataDirectory); }
        catch (Exception ex) { Status("目录本次可用，但未能保存选择：" + ex.Message); }
        await ImportTokensAsync();
    }
    private void CancelTokens(object sender, RoutedEventArgs e) { importCancellation?.Cancel(); TokenStatusText.Text = "正在停止读取…"; }
    private async Task<bool> ImportTokensAsync()
    {
        if (importingTokens || dashboardClosed) return false;
        importingTokens = true; importCancellation = new CancellationTokenSource(); lastTokenImportTick = Stopwatch.GetTimestamp();
        RefreshTokensButton.IsEnabled = false; ChooseCodexFolderButton.IsEnabled = false; TokenYearNavigation.IsEnabled = false; CancelTokensButton.Visibility = Visibility.Visible;
        try
        {
            var home = dashboardPreferences.ResolveCodexHome();
            CodexSourceText.Text = "当前图表来源：" + (tokenImporter.ActiveSource ?? "尚无缓存") + "\n正在读取：" + home;
            TokenStatusText.Text = "正在读取本机记录并去重…首次导入历史日志可能需要一会儿，不影响专注计时。";
            var result = await tokenImporter.ImportAsync(home, importCancellation.Token);
            if (dashboardClosed) return false;
            RefreshTokenUi();
            ReloadPlanetTokens(); var planetSaved = RefreshPlanet();
            CodexSourceText.Text = "当前图表来源：" + result.SourceRoot;
            TokenStatusText.Text = $"已更新 {result.CompletedAt.ToLocalTime():HH:mm:ss} · {result.DaysWithUsage} 天记录 · 本次读取 {result.FilesScanned} 个文件，跳过 {result.FilesUnchanged} 个未变化文件" + (result.ReadErrors > 0 ? $" · {result.ReadErrors} 个文件未能读取" : "");
            return result.ReadErrors == 0 && planetSaved;
        }
        catch (OperationCanceledException) { if (!dashboardClosed) { TokenStatusText.Text = "已取消读取，图表保留上次显示结果。"; CodexSourceText.Text = "当前图表来源：" + (tokenImporter.ActiveSource ?? "尚无缓存"); } return false; }
        catch (Exception ex) { if (!dashboardClosed) { TokenStatusText.Text = "本机用量暂不可用：" + ex.Message; CodexSourceText.Text = "当前图表来源：" + (tokenImporter.ActiveSource ?? "尚无缓存") + "\n待读取目录：" + dashboardPreferences.ResolveCodexHome(); } return false; }
        finally
        {
            importingTokens = false; lastTokenImportTick = Stopwatch.GetTimestamp(); importCancellation?.Dispose(); importCancellation = null;
            if (dashboardClosed) tokenImporter.Dispose();
            else { RefreshTokensButton.IsEnabled = true; ChooseCodexFolderButton.IsEnabled = true; TokenYearNavigation.IsEnabled = true; CancelTokensButton.Visibility = Visibility.Collapsed; }
        }
    }
}
