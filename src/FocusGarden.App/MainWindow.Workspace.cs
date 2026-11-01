using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FocusGarden.Core;

namespace FocusGarden.App;

public partial class MainWindow
{
    private Func<FocusApplicationPolicy, nint, CancellationToken, Task<IFocusWorkspaceSession>> workspaceFactory =
        FocusWorkspaceGuard.StartAsync;
    private IFocusWorkspaceSession? workspaceSession;
    private CancellationTokenSource? workspaceStartCancellation;
    private WorkspaceDockWindow? workspaceDock;
    private IReadOnlyList<WorkspaceBackdropWindow> workspaceBackdrops = [];
    private bool workspaceWindowsShown;
    private long lastWorkspaceRefresh;
    private bool refreshingAllowedApps;
    private string? workspaceReleaseWarning;

    private void LoadWorkspaceSettings()
    {
        WhitelistEnabledCheck.IsChecked = settings.WhitelistEnabled;
        AllowedAppsInput.Text = string.Join(Environment.NewLine, settings.AllowedApps);
        HideTaskbarCheck.IsChecked = settings.HideTaskbar;
        BlockDesktopShortcutsCheck.IsChecked = settings.BlockDesktopShortcuts;
        UpdateWhitelistSummary();
    }

    private void WhitelistOptionsChanged(object sender, RoutedEventArgs e) => UpdateWhitelistSummary();
    private void AllowedAppsChanged(object sender, TextChangedEventArgs e) => UpdateWhitelistSummary();
    private void UpdateWhitelistSummary()
    {
        if (AllowedAppsInput == null || WhitelistSummary == null || WhitelistEnabledCheck == null) return;
        var apps = Lines(AllowedAppsInput.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        WhitelistSummary.Text = WhitelistEnabledCheck.IsChecked == true
            ? apps.Length == 0 ? "尚未选择应用。请先打开工作应用，再配置白名单。"
                : $"已选 {apps.Length} 个应用 · 桌面保持专注画面，小面板可切换应用。"
            : "白名单未启用；原有全屏和应用黑名单设置继续生效。";
    }

    private async void ShowWhitelistSettings(object sender, RoutedEventArgs e)
    {
        Page("rules");
        await RefreshAllowedAppsAsync();
        WhitelistRulesCard.BringIntoView();
    }
    private async void RefreshAllowedApps(object sender, RoutedEventArgs e) => await RefreshAllowedAppsAsync();
    private async Task RefreshAllowedAppsAsync()
    {
        if (refreshingAllowedApps || shuttingDown) return;
        refreshingAllowedApps = true;
        try
        {
            var names = await Task.Run(() =>
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0) continue;
                            result.Add(AppRules.NormalizeAllowed(process.ProcessName) + ".exe");
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                }
                return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (shuttingDown) return;
            var selected = RunningAllowedApps.SelectedItem as string;
            RunningAllowedApps.ItemsSource = names;
            RunningAllowedApps.SelectedItem = selected;
            if (RunningAllowedApps.SelectedIndex < 0 && names.Length > 0) RunningAllowedApps.SelectedIndex = 0;
        }
        catch (Exception error) { if (!shuttingDown) Status("读取运行应用失败：" + error.Message); }
        finally { refreshingAllowedApps = false; }
    }
    private void AddAllowedApp(object sender, RoutedEventArgs e)
    {
        if (clock != null || startingFocus || endingFocus || shuttingDown) return;
        if (RunningAllowedApps.SelectedItem is string app)
            AllowedAppsInput.Text = string.Join(Environment.NewLine, Lines(AllowedAppsInput.Text).Append(app).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<bool> PrepareWorkspaceAsync(FocusApplicationPolicy policy, long generation)
    {
        if (!policy.WhitelistEnabled) return true;
        var cancellation = new CancellationTokenSource();
        workspaceStartCancellation = cancellation;
        IFocusWorkspaceSession? prepared = null;
        try
        {
            Status("正在准备白名单工作区…");
            prepared = await workspaceFactory(policy, new WindowInteropHelper(this).Handle, cancellation.Token);
            if (prepared == null) throw new InvalidOperationException("白名单控制器未就绪。");
            if (cancellation.IsCancellationRequested || StartWasInterrupted(generation)) return false;
            workspaceSession = prepared; prepared = null;
            lastWorkspaceRefresh = 0;
            workspaceReleaseWarning = null;
            return true;
        }
        catch (OperationCanceledException) { Status("开始已取消，任务栏和键盘已恢复。"); return false; }
        catch (Exception error) { Status("白名单未能启动，已取消本轮：" + error.Message); return false; }
        finally
        {
            if (prepared != null) try { prepared.Dispose(); } catch (Exception error) { Status("本轮未启动；恢复桌面时遇到问题：" + error.Message); }
            cancellation.Dispose(); workspaceStartCancellation = null;
        }
    }

    private void ShowWorkspaceDock()
    {
        if (workspaceSession == null || clock == null && !DailyActive) return;
        var current = workspaceSession;
        workspaceWindowsShown = IsVisible;
        Action emergency = () => { if (ReferenceEquals(workspaceSession, current)) ConfirmEmergency(); };
        // No HWND creation or monitor manipulation in hidden synthetic fixtures.
        workspaceBackdrops = workspaceWindowsShown
            ? WorkspaceBackdropWindow.CreateForDisplays(currentTask, appearance.BackgroundImage, emergency, IsStrictFocusActive || DailyActive)
            : [new WorkspaceBackdropWindow(currentTask, appearance.BackgroundImage, emergency, IsStrictFocusActive || DailyActive)];
        foreach (var backdrop in workspaceBackdrops)
        {
            backdrop.DisplayConfigurationChanged += (_, _) =>
            {
                if (ReferenceEquals(workspaceSession, current))
                    Finish("interrupted", "显示器配置发生变化，本轮已中断并恢复桌面；已保存的时长保留。");
            };
            if (DailyActive)
                backdrop.ConfigureDaily((task, minutes) => ReferenceEquals(workspaceSession, current) ? StartDailyTaskAsync(task, minutes) : Task.FromResult<string?>("这轮已结束。"),
                    () => { if (ReferenceEquals(workspaceSession, current)) StopDailyTask("emergency", "任务钟已停止，全天专注继续。"); });
            backdrop.UpdateTime(WorkspaceCountdown);
            if (workspaceWindowsShown) backdrop.Show();
        }
        current.SetBackdropWindows(workspaceWindowsShown
            ? workspaceBackdrops.Select(window => new WindowInteropHelper(window).Handle).ToArray()
            : []);
        workspaceDock = new WorkspaceDockWindow(
            handle => { if (ReferenceEquals(workspaceSession, current)) ActivateWorkspaceApp(handle); },
            () => { if (ReferenceEquals(workspaceSession, current)) ShowWorkspaceFocus(); },
            () => { if (ReferenceEquals(workspaceSession, current)) ConfirmEmergency(); }, IsStrictFocusActive || DailyActive,
            path => { if (ReferenceEquals(workspaceSession, current)) LaunchDailyApp(path); }, DailyActive);
        RefreshWorkspaceDock();
        // Hidden synthetic fixtures never show a window or register a desktop hotkey.
        // Keep the switcher available even when the user minimizes the main window.
        if (workspaceWindowsShown) workspaceDock.Show();
        var first = current.GetAllowedWindows().FirstOrDefault();
        if (workspaceWindowsShown && first != null) current.ActivateAllowedWindow(first.Handle);
        else if (workspaceWindowsShown) ShowWorkspaceFocus();
    }

    private void ShowWorkspaceFocus()
    {
        if (workspaceSession == null || workspaceBackdrops.Count == 0) return;
        var backdrop = workspaceBackdrops[0];
        if (workspaceWindowsShown)
        {
            if (backdrop.WindowState == WindowState.Minimized) backdrop.WindowState = WindowState.Normal;
            backdrop.Show(); backdrop.Activate();
        }
    }

    private void ActivateWorkspaceApp(nint handle)
    {
        if (workspaceSession == null) return;
        try
        {
            if (!workspaceSession.ActivateAllowedWindow(handle))
            {
                Status("这个应用窗口已关闭或暂时不能切换，请选择其他白名单应用。");
                RefreshWorkspaceDock();
            }
        }
        catch (Exception error) { Finish("interrupted", "白名单控制已中断，限制已解除：" + error.Message); }
    }

    private void PulseWorkspace()
    {
        if (workspaceSession == null) return;
        try
        {
            workspaceSession.Pulse();
            // Show Desktop may minimize tool windows too. Restore only our switcher,
            // without activating it or reopening a deliberately minimized work app.
            if (workspaceWindowsShown && workspaceDock is { } dock)
            {
                if (dock.WindowState == WindowState.Minimized) dock.WindowState = WindowState.Normal;
                if (!dock.IsVisible) dock.Show();
            }
            workspaceDock?.UpdateTime(WorkspaceCountdown);
            if (Stopwatch.GetElapsedTime(lastWorkspaceRefresh).TotalSeconds >= 2) RefreshWorkspaceDock();
        }
        catch (Exception error) { Finish("interrupted", "白名单保护已恢复桌面，本轮中断，已保存的时长保留：" + error.Message); }
    }
    private void RefreshWorkspaceDock()
    {
        if (workspaceSession == null || workspaceDock == null) return;
        lastWorkspaceRefresh = Stopwatch.GetTimestamp();
        var windows = workspaceSession.GetAllowedWindows();
        var groups = windows.GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        workspaceDock.SetWindows(windows.Select(window =>
        {
            occurrence[window.ProcessName] = occurrence.GetValueOrDefault(window.ProcessName) + 1;
            var label = AppDisplayName(window.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? window.ProcessName : window.ProcessName + ".exe");
            if (groups[window.ProcessName] > 1) label += $" · 窗口 {occurrence[window.ProcessName]}";
            return new WorkspaceWindowChoice(window.Handle, label);
        }).Concat(DailyLaunchChoices(windows)).ToArray());
        workspaceDock.UpdateTime(WorkspaceCountdown);
    }
    internal void ReleaseWorkspace()
    {
        workspaceWindowsShown = false;
        try { workspaceStartCancellation?.Cancel(); }
        catch (Exception error) { workspaceReleaseWarning = error.Message; }
        var session = workspaceSession; workspaceSession = null;
        try { session?.Dispose(); }
        catch (Exception error) { workspaceReleaseWarning = "桌面恢复报告异常，恢复助手将继续处理：" + error.Message; }
        var dock = workspaceDock; workspaceDock = null;
        try { dock?.Release(); } catch (Exception error) { workspaceReleaseWarning ??= error.Message; }
        var backdrops = workspaceBackdrops; workspaceBackdrops = [];
        foreach (var backdrop in backdrops)
            try { backdrop.Release(); } catch (Exception error) { workspaceReleaseWarning ??= error.Message; }
    }

    private async Task CancelUnstartedSessionAsync()
    {
        if (sessionId is not long id) return;
        sessionId = null; sessionWriter = null;
        try { await focusWork.RunAsync(() => { store.EndSession(id, "interrupted", DateTimeOffset.Now); Interlocked.Increment(ref focusDataRevision); }); }
        catch (Exception error) { Status("本轮未开始；零秒记录将在下次启动恢复为中断：" + error.Message); }
    }
}
