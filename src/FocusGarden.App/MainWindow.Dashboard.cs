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
    private readonly SerialWorkQueue tokenReadWork = new();
    private Task usageRefreshTask = Task.CompletedTask, tokenRefreshTask = Task.CompletedTask;
    private Task<bool>? tokenImportTask;
    private Task? dashboardCloseTask;
    private bool usageRefreshAgain, tokenRefreshAgain, dashboardDisposed;
    private string? cachedTokenSource;
    private int cachedTokenYear, tokenDataGeneration;
    private DateTime cachedTokenDate;
    private Dictionary<string, long> cachedTokenDaily = [], cachedTokenToday = [];
    private string? tokenReadError;
    private readonly Dictionary<string, UsageRowVisual> usageVisuals = new(StringComparer.OrdinalIgnoreCase);
    private TextBlock? noUsageVisual;
    private bool IsDashboardVisible => FocusPage.Visibility == Visibility.Visible && WindowState != WindowState.Minimized && immersion == null;

    private void InitializeDashboard()
    {
        dashboardPreferences = DashboardPreferences.Load(App.DataDirectory);
        usageStore = new AppUsageStore(Path.Combine(App.DataDirectory, "focus.db"));
        usageTracker = new AppUsageTracker(usageStore, focusWork) { Enabled = dashboardPreferences.TrackApplications };
        tokenImporter = new CodexUsageImporter(Path.Combine(App.DataDirectory, "codex-usage.db"));
        // Startup only: imports have not started yet. Runtime reads always use the background queue.
        cachedTokenSource = tokenImporter.ActiveSource;
        UsageEnabledCheck.IsChecked = dashboardPreferences.TrackApplications;
        AutoRefreshTokensCheck.IsChecked = dashboardPreferences.AutoRefreshTokens;
        dashboardReady = true; dashboardTick = Stopwatch.GetTimestamp();
        TokenStatusText.Text = "启动时将读取本机用量；也可手动刷新。首次读取历史日志可能需要一会儿。";
        CodexSourceText.Text = "当前图表来源：" + (cachedTokenSource ?? "尚无缓存") + "\n读取目录：" + dashboardPreferences.ResolveCodexHome();
        FocusPage.IsVisibleChanged += (_, _) => { if (IsDashboardVisible) { RefreshUsageUi(); RefreshTokenUi(); } };
        StateChanged += (_, _) => { if (IsDashboardVisible) { RefreshUsageUi(); RefreshTokenUi(); } };
        Loaded += async (_, _) =>
        {
            // Start UI-bound continuations only after WPF has entered its dispatcher loop.
            RefreshUsageUi();
            await RefreshTokenDataAsync();
            await ImportTokensAsync();
        };
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
        if (dashboardPreferences.AutoRefreshTokens && !importingTokens && !travellingPlanet && Stopwatch.GetElapsedTime(lastTokenImportTick).TotalSeconds >= 120) _ = ImportTokensAsync();
    }
    private void CloseDashboard() => _ = CloseDashboardAsync();
    private Task CloseDashboardAsync()
    {
        if (dashboardDisposed) return Task.CompletedTask;
        if (dashboardCloseTask is null || dashboardCloseTask.IsFaulted || dashboardCloseTask.IsCanceled)
            dashboardCloseTask = CloseDashboardCoreAsync();
        return dashboardCloseTask;
    }
    private async Task CloseDashboardCoreAsync()
    {
        dashboardClosed = true;
        importCancellation?.Cancel();
        if (tokenImportTask != null) await tokenImportTask;
        await Task.WhenAll(usageRefreshTask, tokenRefreshTask);
        if (dashboardReady)
        {
            if (!await usageTracker.FlushAllAsync()) throw new IOException(usageTracker.LastError ?? "应用时长尚未保存，请重试关闭。");
            await focusWork.RunAsync(usageStore.Dispose);
        }
        await tokenReadWork.DisposeAsync();
        if (dashboardReady) await Task.Run(tokenImporter.Dispose);
        dashboardDisposed = true;
    }
    private async Task DrainDashboardAsync()
    {
        if (!dashboardReady) return;
        await Task.WhenAll(usageRefreshTask, tokenRefreshTask);
        if (tokenImportTask != null) await tokenImportTask;
        await Task.WhenAll(usageRefreshTask, tokenRefreshTask);
        if (!await usageTracker.FlushAllAsync()) throw new IOException(usageTracker.LastError ?? "应用时长尚未保存，请重试。");
        await tokenReadWork.DrainAsync();
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
    private async void ToggleUsage(object sender, RoutedEventArgs e)
    {
        if (!dashboardReady || dashboardClosed) return;
        dashboardPreferences.TrackApplications = UsageEnabledCheck.IsChecked == true;
        usageTracker.Enabled = dashboardPreferences.TrackApplications;
        try { await SaveDashboardPreferencesAsync(); }
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
        if (!dashboardReady || dashboardClosed || !IsDashboardVisible) return;
        usageRefreshAgain = true;
        if (usageRefreshTask.IsCompleted) usageRefreshTask = RefreshUsageLoopAsync();
    }
    private async Task RefreshUsageLoopAsync()
    {
        while (usageRefreshAgain && !dashboardClosed && IsDashboardVisible)
        {
            usageRefreshAgain = false;
            var requestedDate = usageDate;
            try
            {
                var rows = await usageTracker.GetDailyUsageAsync(requestedDate);
                if (dashboardClosed || !IsDashboardVisible) return;
                if (requestedDate != usageDate) { usageRefreshAgain = true; continue; }
                RenderUsageRows(requestedDate, rows);
            }
            catch (Exception ex) { if (!dashboardClosed) UsageStatusText.Text = "应用时长读取失败：" + ex.Message; }
        }
    }
    private void RenderUsageRows(DateTime date, IReadOnlyList<AppUsageRow> rows)
    {
        UsageDateLabel.Text = date.ToString("yyyy-MM-dd") + (date == DateTime.Today ? " · 今天" : "");
        var total = rows.Sum(x => x.Seconds);
        UsageSummary.Text = "已记录 " + Duration(total) + " · " + rows.Count + " 个应用";
        var present = rows.Select(r => r.AppName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in usageVisuals.Keys.Where(k => !present.Contains(k)).ToArray())
        {
            UsageRows.Children.Remove(usageVisuals[removed].Panel);
            usageVisuals.Remove(removed);
        }
        noUsageVisual ??= new TextBlock { Text = "这一天还没有应用使用记录。记录从本程序运行后开始，不补算此前时长。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 10, 0, 8) };
        if (rows.Count == 0)
        {
            if (!UsageRows.Children.Contains(noUsageVisual)) UsageRows.Children.Add(noUsageVisual);
        }
        else UsageRows.Children.Remove(noUsageVisual);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!usageVisuals.TryGetValue(row.AppName, out var visual))
            {
                var panel = new StackPanel { Margin = new Thickness(0, 7, 0, 4) };
                var line = new DockPanel();
                var time = new TextBlock { Foreground = (Brush)FindResource("Green"), FontSize = 12, Margin = new Thickness(14, 0, 0, 0) };
                DockPanel.SetDock(time, Dock.Right); line.Children.Add(time);
                line.Children.Add(new TextBlock { Text = AppDisplayName(row.AppName), ToolTip = row.AppName, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                var bar = new ProgressBar { Height = 3, Margin = new Thickness(0, 6, 0, 0), Background = new SolidColorBrush(Color.FromRgb(39, 59, 65)), Foreground = (Brush)FindResource("Green"), BorderThickness = new Thickness(0) };
                panel.Children.Add(line); panel.Children.Add(bar);
                usageVisuals.Add(row.AppName, visual = new(panel, time, bar));
            }
            var duration = Duration(row.Seconds);
            if (visual.Time.Text != duration) visual.Time.Text = duration;
            visual.Bar.Maximum = Math.Max(total, 1); visual.Bar.Value = row.Seconds;
            if (UsageRows.Children.IndexOf(visual.Panel) != index)
            {
                UsageRows.Children.Remove(visual.Panel);
                UsageRows.Children.Insert(index, visual.Panel);
            }
        }
        UsageStatusText.Text = usageTracker.LastError ?? usageTracker.StateText;
    }
    private sealed record UsageRowVisual(StackPanel Panel, TextBlock Time, ProgressBar Bar);
    private static string TokenNumber(long value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000.0:0.##}B" : value >= 1_000_000 ? $"{value / 1_000_000.0:0.##}M" : value >= 1_000 ? $"{value / 1_000.0:0.##}K" : value.ToString("N0");
    private void RefreshTokenUi()
    {
        if (!dashboardReady || dashboardClosed) return;
        if (cachedTokenYear != tokenYear || cachedTokenDate != DateTime.Today) { _ = RefreshTokenDataAsync(); return; }
        if (!IsDashboardVisible) return;
        if (tokenCalendar == null) { tokenCalendar = new TokenCalendar3D(deferInitialScene: true); TokenCalendarHost.Content = tokenCalendar; }
        var daily = cachedTokenDaily;
        var today = cachedTokenToday;
        today.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out var todayTokens);
        long yearTotal = daily.Values.Sum();
        TokenYearLabel.Text = tokenYear.ToString();
        TokenYearTotal.Text = daily.Count == 0 ? "暂无记录" : TokenNumber(yearTotal); TokenYearTotal.ToolTip = yearTotal.ToString("N0") + " tokens";
        TokenTodayTotal.Text = today.ContainsKey(DateTime.Today.ToString("yyyy-MM-dd")) ? TokenNumber(todayTokens) : "暂无记录"; TokenTodayTotal.ToolTip = todayTokens.ToString("N0") + " tokens";
        TokenDays.Text = daily.Count.ToString(); tokenCalendar.SetData(tokenYear, daily);
    }
    private Task RefreshTokenDataAsync()
    {
        if (!dashboardReady || dashboardClosed) return Task.CompletedTask;
        tokenRefreshAgain = true;
        if (tokenRefreshTask.IsCompleted) tokenRefreshTask = RefreshTokenLoopAsync();
        return tokenRefreshTask;
    }
    private async Task RefreshTokenLoopAsync()
    {
        while (tokenRefreshAgain && !dashboardClosed)
        {
            tokenRefreshAgain = false;
            var requestedYear = tokenYear; var requestedDay = DateTime.Today; var generation = tokenDataGeneration;
            try
            {
                var snapshot = await tokenReadWork.RunAsync(() =>
                {
                    var source = tokenImporter.ActiveSource;
                    var daily = tokenImporter.GetDailyTotals(requestedYear);
                    var today = requestedYear == requestedDay.Year ? daily : tokenImporter.GetDailyTotals(requestedDay.Year);
                    if (!string.Equals(source, tokenImporter.ActiveSource, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("用量来源已变化，稍后重新读取。");
                    return (Source: source, Daily: daily, Today: today);
                });
                if (dashboardClosed) return;
                if (generation != tokenDataGeneration) continue;
                if (requestedYear != tokenYear || requestedDay != DateTime.Today) { tokenRefreshAgain = true; continue; }
                cachedTokenSource = snapshot.Source;
                cachedTokenYear = requestedYear; cachedTokenDate = requestedDay;
                cachedTokenDaily = snapshot.Daily; cachedTokenToday = snapshot.Today;
                tokenReadError = null;
                RefreshTokenUi();
            }
            catch (Exception ex) { tokenReadError = ex.Message; if (!dashboardClosed) TokenStatusText.Text = "用量缓存暂不可用：" + ex.Message; }
        }
    }
    private Task<Dictionary<string, long>> GetPlanetTokenTotalsAsync(DateTimeOffset cutoff, DateTimeOffset now)
    {
        if (dashboardClosed) return Task.FromException<Dictionary<string, long>>(new ObjectDisposedException("应用用量服务"));
        var expectedSource = cachedTokenSource;
        return tokenReadWork.RunAsync(() =>
        {
            if (!string.Equals(expectedSource, tokenImporter.ActiveSource, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("用量来源正在切换，请稍后重试。");
            var totals = tokenImporter.GetDailyTotalsSince(cutoff, now);
            if (!string.Equals(expectedSource, tokenImporter.ActiveSource, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("用量来源已切换，本次成长未更新。");
            return totals;
        });
    }
    private Task SaveDashboardPreferencesAsync()
    {
        var snapshot = new DashboardPreferences { TrackApplications = dashboardPreferences.TrackApplications, AutoRefreshTokens = dashboardPreferences.AutoRefreshTokens, CodexHome = dashboardPreferences.CodexHome };
        return tokenReadWork.RunAsync(() => snapshot.Save(App.DataDirectory));
    }
    private void PreviousTokenYear(object sender, RoutedEventArgs e) { if (tokenYear > 2000 && !importingTokens) { tokenYear--; RefreshTokenUi(); } }
    private void NextTokenYear(object sender, RoutedEventArgs e) { if (tokenYear < DateTime.Today.Year && !importingTokens) { tokenYear++; RefreshTokenUi(); } }
    private async void RefreshTokensClick(object sender, RoutedEventArgs e) { if (!travellingPlanet) await ImportTokensAsync(); }
    private async void ToggleTokenRefresh(object sender, RoutedEventArgs e)
    {
        if (!dashboardReady || dashboardClosed) return;
        dashboardPreferences.AutoRefreshTokens = AutoRefreshTokensCheck.IsChecked == true;
        lastTokenImportTick = Stopwatch.GetTimestamp();
        try { await SaveDashboardPreferencesAsync(); }
        catch (Exception ex) { Status("刷新开关已生效，但未能保存设置：" + ex.Message); }
    }
    private async void ChooseCodexFolder(object sender, RoutedEventArgs e)
    {
        if (importingTokens || travellingPlanet || dashboardClosed) return;
        var dialog = new OpenFolderDialog { Title = "选择包含 sessions 或 archived_sessions 的 Codex 数据目录" };
        if (dialog.ShowDialog(this) != true) return;
        if (!Directory.Exists(Path.Combine(dialog.FolderName, "sessions")) && !Directory.Exists(Path.Combine(dialog.FolderName, "archived_sessions"))) { Status("所选目录不含 sessions 或 archived_sessions，请选择 Codex 数据根目录。"); return; }
        dashboardPreferences.CodexHome = dialog.FolderName;
        try { await SaveDashboardPreferencesAsync(); }
        catch (Exception ex) { Status("目录本次可用，但未能保存选择：" + ex.Message); }
        await ImportTokensAsync();
    }
    private void CancelTokens(object sender, RoutedEventArgs e) { importCancellation?.Cancel(); TokenStatusText.Text = "正在停止读取…"; }
    private Task<bool> ImportTokensAsync()
    {
        if (importingTokens || dashboardClosed) return Task.FromResult(false);
        importingTokens = true; tokenDataGeneration++;
        return tokenImportTask = ImportTokensCoreAsync();
    }
    private async Task<bool> ImportTokensCoreAsync()
    {
        importCancellation = new CancellationTokenSource(); lastTokenImportTick = Stopwatch.GetTimestamp();
        RefreshTokensButton.IsEnabled = false; ChooseCodexFolderButton.IsEnabled = false; TokenYearNavigation.IsEnabled = false; CancelTokensButton.Visibility = Visibility.Visible;
        try
        {
            var home = dashboardPreferences.ResolveCodexHome();
            CodexSourceText.Text = "当前图表来源：" + (cachedTokenSource ?? "尚无缓存") + "\n正在读取：" + home;
            TokenStatusText.Text = "正在读取本机记录并去重…首次导入历史日志可能需要一会儿，不影响专注计时。";
            var result = await tokenImporter.ImportAsync(home, importCancellation.Token);
            if (dashboardClosed) return false;
            cachedTokenSource = result.SourceRoot;
            await RefreshTokenDataAsync();
            if (dashboardClosed) return false;
            if (tokenReadError != null || cachedTokenYear != tokenYear || cachedTokenDate != DateTime.Today)
                throw new InvalidOperationException(tokenReadError ?? "用量图表正在切换，请稍后重新刷新。");
            await ReloadPlanetTokensAsync(); var planetSaved = await RefreshPlanetAsync();
            if (dashboardClosed) return false;
            CodexSourceText.Text = "当前图表来源：" + result.SourceRoot;
            TokenStatusText.Text = $"已更新 {result.CompletedAt.ToLocalTime():HH:mm:ss} · {result.DaysWithUsage} 天记录 · 本次读取 {result.FilesScanned} 个文件，跳过 {result.FilesUnchanged} 个未变化文件" + (result.ReadErrors > 0 ? $" · {result.ReadErrors} 个文件未能读取" : "");
            return result.ReadErrors == 0 && planetSaved;
        }
        catch (OperationCanceledException) { if (!dashboardClosed) { TokenStatusText.Text = "已取消读取，图表保留上次显示结果。"; CodexSourceText.Text = "当前图表来源：" + (cachedTokenSource ?? "尚无缓存"); } return false; }
        catch (Exception ex) { if (!dashboardClosed) { TokenStatusText.Text = "本机用量暂不可用：" + ex.Message; CodexSourceText.Text = "当前图表来源：" + (cachedTokenSource ?? "尚无缓存") + "\n待读取目录：" + dashboardPreferences.ResolveCodexHome(); } return false; }
        finally
        {
            importingTokens = false; lastTokenImportTick = Stopwatch.GetTimestamp(); importCancellation?.Dispose(); importCancellation = null;
            if (!dashboardClosed) { RefreshTokensButton.IsEnabled = true; ChooseCodexFolderButton.IsEnabled = true; TokenYearNavigation.IsEnabled = true; CancelTokensButton.Visibility = Visibility.Collapsed; }
        }
    }
}
