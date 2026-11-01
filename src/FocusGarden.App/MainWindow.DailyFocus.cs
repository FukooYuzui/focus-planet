using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FocusGarden.Core;
using FocusGarden.Timing;

namespace FocusGarden.App;

public partial class MainWindow
{
    private DailyFocusPlan dailyPlan = new();
    private DailyFocusPlanStore dailyPlanStore = null!;
    private DailyFocusEditor dailyEditor = null!;
    private DailyTaskPanel dailyTaskPanel = null!;
    private DailyFocusPlan? activeDailyPlan;
    private DailyFocusWindow? activeDailyWindow;
    private DailyFocusSlot? activeDailySlot;
    private bool dailyStarting, dailyTaskStarting, dailyPlanSaving, dailyHotkeyRegistered, dailySchedulerReady;
    private bool DailyActive => activeDailyWindow != null;
    private long dailyGeneration, dailyTimestamp;
    private DateTimeOffset? dailyTaskDeadline;
    private string dailyTaskTitle = "", dailyTaskNotice = "输入下一项任务和时长。";
    private string dailyTaskKey = "";
    private GoalContribution? dailyGoalContribution;
    private FocusClock? dailyCreditClock;
    private SessionWriter? dailyWriter;
    private Task dailyStartTask = Task.CompletedTask, dailyWriteTask = Task.CompletedTask, dailyEndTask = Task.CompletedTask, dailyPlanTask = Task.CompletedTask;
    private Func<DateTimeOffset> dailyNow = () => DateTimeOffset.Now;
    private Func<Task> dailyAlarm = DailyAlarm.PlayAsync;

    private void InitializeDailyFocus()
    {
        dailyPlanStore = new DailyFocusPlanStore(App.DataDirectory);
        string? error = null;
        try { dailyPlan = dailyPlanStore.Load(); } catch (Exception ex) { error = "全天计划未加载，未开启限制；原文件已保留：" + ex.Message; }
        dailyEditor = new DailyFocusEditor(SaveDailyPlanAsync, ResumeDailyPlanAsync);
        dailyEditor.Load(dailyPlan);
        dailyTaskPanel = new DailyTaskPanel(StartDailySelectedTaskAsync, () => StopDailyTask("emergency", "任务钟已停止，全天专注继续。"));
        dailyEditor.Children.Add(dailyTaskPanel);
        DailyFocusHost.Content = dailyEditor;
        if (error != null) dailyEditor.StatusText.Text = error;
        Loaded += (_, _) => { dailySchedulerReady = !App.TestMode; };
        UpdateDailyUi();
    }
    private void ShowDailyFocus(object sender, RoutedEventArgs e) => Page("daily");
    private async Task<string?> SaveDailyPlanAsync(DailyFocusPlan candidate)
    {
        if (DailyActive || dailyStarting || dailyPlanSaving || shuttingDown) return "请先强制退出全天模式，再修改计划。";
        candidate = candidate.Snapshot();
        if (candidate.Enabled && !dailyHotkeyRegistered) return "强制退出组合键被占用，全天模式未启用。请释放 Ctrl + Alt + Shift + F12 后重启程序。";
        candidate.SuppressedWindow = dailyPlan.SuppressedWindow;
        dailyPlanSaving = true;
        try
        {
            await (dailyPlanTask = focusWork.RunAsync(() => dailyPlanStore.Save(candidate)));
            dailyPlan = candidate; UpdateDailyUi();
            return "计划已保存；程序保持运行时按计划执行。若普通番茄钟仍在进行，将在其结束后接管。";
        }
        finally { dailyPlanSaving = false; }
    }
    private async Task<string?> ResumeDailyPlanAsync()
    {
        if (DailyActive || dailyStarting || dailyPlanSaving) return "全天模式已经开启或正在准备。";
        if (!dailyHotkeyRegistered) return "强制退出组合键不可用，未重新启用。";
        var candidate = dailyPlan.Snapshot(); candidate.Enabled = true; candidate.SuppressedWindow = null;
        dailyPlanSaving = true;
        try { await (dailyPlanTask = focusWork.RunAsync(() => dailyPlanStore.Save(candidate))); dailyPlan = candidate; dailyEditor.Load(dailyPlan); }
        finally { dailyPlanSaving = false; }
        return "今天的计划已重新启用；在设定时段内将自动开始。";
    }
    private void DailyTick()
    {
        if (shuttingDown) return;
        var now = dailyNow();
        if (!DailyActive)
        {
            if (dailySchedulerReady && dailyPlan.Enabled && !dailyStarting && !dailyPlanSaving && dailyEndTask.IsCompleted &&
                !desktopLocked && !powerSuspended && clock == null && !startingFocus && !endingFocus && !savingSettings && !travellingPlanet)
            {
                var window = dailyPlan.WindowAt(now);
                if (window != null && window.Key != dailyPlan.SuppressedWindow && dailyHotkeyRegistered)
                    dailyStartTask = StartDailyFocusAsync();
            }
            UpdateDailyUi(); return;
        }
        if (dailyStarting) { if (workspaceSession != null) PulseWorkspace(); return; }
        var active = activeDailyWindow!;
        if (now < active.Start || now >= active.End)
        {
            if (dailyTaskDeadline is { } deadline && now >= deadline) _ = RingDailyAlarmAsync();
            StopDailyFocus("completed", "今日全天专注已结束，桌面已恢复。", true); return;
        }
        try
        {
            var slot = activeDailyPlan!.SlotAt(now, active);
            if (!ReferenceEquals(slot, activeDailySlot))
            {
                activeDailySlot = slot;
                workspaceSession!.UpdateScheduledPolicy(DailyPolicy());
                RefreshWorkspaceDock();
            }
            AdvanceDailyRecord(now);
            if (dailyTaskDeadline is { } deadline && now >= deadline)
            {
                StopDailyTask("completed", "「" + dailyTaskTitle + "」时间到了。全天专注继续，可开始下一项任务。");
                _ = RingDailyAlarmAsync();
            }
            bridge.Publish(true, settings.BlockedDomains, Math.Max(1, (int)Math.Ceiling((active.End - now).TotalSeconds)));
            PulseWorkspace(); UpdateDailyUi();
        }
        catch (Exception ex) { StopDailyFocus("interrupted", "全天专注已安全中断：" + ex.Message, true); }
    }
    private FocusApplicationPolicy DailyPolicy() => FocusApplicationPolicy.CreateScheduled(activeDailySlot?.AppNames() ?? [],
        activeDailySlot == null && activeDailyPlan!.AllowUnplannedApps);

    private async Task StartDailyFocusAsync()
    {
        if (DailyActive || dailyStarting || shuttingDown || desktopLocked || powerSuspended || clock != null || startingFocus || endingFocus || !dailyEndTask.IsCompleted) return;
        var window = dailyPlan.WindowAt(dailyNow());
        if (!dailyPlan.Enabled || window == null || window.Key == dailyPlan.SuppressedWindow || !dailyHotkeyRegistered) return;
        dailyStarting = true;
        UpdateBoardUi();
        var generation = ++dailyGeneration;
        activeDailyPlan = dailyPlan.Snapshot(); activeDailyWindow = window;
        activeDailySlot = activeDailyPlan.SlotAt(dailyNow(), window);
        dailyTaskNotice = "输入下一项任务和时长。";
        dailyEditor.SetActive(true);
        try
        {
            await FlushFocusBoardAsync();
            if (generation != dailyGeneration || desktopLocked || powerSuspended || dailyNow() >= window.End)
            {
                if (generation == dailyGeneration) StopDailyFocus("interrupted", "全天模式准备期间已中断，今天已暂停。", true);
                return;
            }
            if (!await PrepareWorkspaceAsync(DailyPolicy(), Volatile.Read(ref sessionEventGeneration)) ||
                generation != dailyGeneration || desktopLocked || powerSuspended || dailyNow() >= window.End)
            {
                if (generation == dailyGeneration) StopDailyFocus("interrupted", "全天专注未能开始，今天已暂停；可在计划页重新启用。", true);
                return;
            }
            ShowWorkspaceDock(); SetActiveUi(true); Page("daily");
            if (activeDailyPlan!.CountWholePeriod)
            {
                var seconds = Math.Clamp((int)Math.Ceiling((window.End - dailyNow()).TotalSeconds), 1, 86400);
                var id = await focusWork.RunAsync(() => store.BeginSession("全天专注", seconds, dailyNow()));
                if (generation != dailyGeneration) { await focusWork.RunAsync(() => store.EndSession(id, "interrupted", dailyNow())); return; }
                dailyWriter = new SessionWriter(id); dailyCreditClock = new FocusClock(seconds);
            }
            dailyTimestamp = Stopwatch.GetTimestamp();
            Status("全天专注已开启；Ctrl + Alt + Shift + F12 可强制退出，任务钟到点不会解除限制。");
        }
        catch (Exception ex) { if (generation == dailyGeneration) StopDailyFocus("interrupted", "全天模式未能开启：" + ex.Message, true); }
        finally { dailyStarting = false; UpdateDailyUi(); UpdateBoardUi(); }
    }
    private Task<string?> StartDailyTaskAsync(string task, int minutes) => StartDailySelectedTaskAsync(task, minutes, null);
    private async Task<string?> StartDailySelectedTaskAsync(string task, int minutes, GoalContribution? contribution)
    {
        if (!DailyActive || dailyStarting || dailyTaskStarting || !dailyEndTask.IsCompleted) return "请等待全天模式准备和上一项记录保存完成。";
        if (dailyTaskDeadline != null) return "已有任务钟正在运行，请先停止它。";
        task = task.Trim();
        if (task.Length == 0 && contribution != null) task = boardSnapshot.Goals.FirstOrDefault(x => x.Id == contribution.GoalId)?.Title ?? "";
        if (task.Length is < 1 or > 200) return "请填写 1–200 个字的任务。";
        if (minutes is < 1 or > 720) return "时长请输入 1–720 分钟。";
        if (dailyNow().AddMinutes(minutes) > activeDailyWindow!.End) return "任务时长不能超过今日全天模式的结束时间。";
        dailyTaskStarting = true;
        var generation = dailyGeneration;
        try
        {
            await boardTask;
            if (contribution != null)
            {
                contribution = contribution with { PartIds = contribution.PartIds.ToArray() };
                try { await focusWork.RunAsync(() => boardStore!.ValidateContribution(contribution)); }
                catch (Exception ex) { return ex.Message; }
            }
            if (generation != dailyGeneration || !DailyActive) return "这轮全天专注已结束。";
            if (!activeDailyPlan!.CountWholePeriod)
            {
                var started = dailyNow();
                var id = await focusWork.RunAsync(() => store.BeginSession(task, minutes * 60, started));
                if (generation != dailyGeneration || !DailyActive || dailyNow().AddMinutes(minutes) > activeDailyWindow!.End)
                { await focusWork.RunAsync(() => store.EndSession(id, "interrupted", dailyNow())); return "全天模式已结束或剩余时间不足，任务钟未开始。"; }
                dailyWriter = new SessionWriter(id); dailyCreditClock = new FocusClock(minutes * 60);
            }
            dailyTaskTitle = task; dailyTaskDeadline = dailyNow().AddMinutes(minutes);
            dailyGoalContribution = contribution;
            dailyTaskKey = activeDailyPlan!.CountWholePeriod ? "task:" + Guid.NewGuid().ToString("N") : "session:" + dailyWriter!.Id;
            if (!activeDailyPlan!.CountWholePeriod) dailyTimestamp = Stopwatch.GetTimestamp();
            dailyTaskNotice = ""; UpdateDailyUi(); return null;
        }
        catch (Exception ex) { StopDailyFocus("interrupted", "任务记录无法保存，已解除全天限制：" + ex.Message, true); return ex.Message; }
        finally { dailyTaskStarting = false; }
    }
    private void AdvanceDailyRecord(DateTimeOffset now)
    {
        var stamp = Stopwatch.GetTimestamp(); var delta = Stopwatch.GetElapsedTime(dailyTimestamp, stamp).TotalSeconds; dailyTimestamp = stamp;
        if (dailyCreditClock == null || dailyWriter == null || dailyWriter.Failed) return;
        var cutoff = activeDailyWindow?.End ?? now;
        if (activeDailyPlan?.CountWholePeriod != true && dailyTaskDeadline is { } deadline && deadline < cutoff) cutoff = deadline;
        // Reject ambiguous stalls before clipping a timely callback to a wall-clock deadline.
        if (delta <= 5) delta = Math.Max(0, delta - Math.Max(0, (now - cutoff).TotalSeconds));
        var intervalEnd = now > cutoff ? cutoff : now;
        var seconds = dailyCreditClock.Advance(delta);
        if (dailyCreditClock.GapDetected) dailyCreditClock.ContinueAfterGap();
        if (seconds <= 0) return;
        var writer = dailyWriter;
        var write = focusWork.RunAsync(() =>
        {
            if (writer.Failed) throw new IOException("之前的计时保存失败，已停止继续写入。");
            try { store.AddProgress(writer.Id, seconds, intervalEnd); Interlocked.Increment(ref focusDataRevision); }
            catch { writer.Failed = true; throw; }
        });
        dailyWriteTask = ObserveDailyWriteAsync(write, writer);
        if (dailyCreditClock.ElapsedSeconds % 30 == 0) RefreshStatistics();
    }
    private async Task ObserveDailyWriteAsync(Task write, SessionWriter writer)
    {
        try { await write; }
        catch (Exception ex)
        {
            writer.Failed = true;
            if (ReferenceEquals(writer, dailyWriter)) StopDailyFocus("interrupted", "保存失败，全天限制已解除；已保存时长保留：" + ex.Message, true);
        }
    }
    private void StopDailyTask(string status, string notice)
    {
        if (dailyTaskDeadline == null) return;
        try { AdvanceDailyRecord(dailyNow()); }
        catch (Exception ex) { StopDailyFocus("interrupted", "任务保存失败，已解除限制：" + ex.Message, true); return; }
        dailyTaskDeadline = null; dailyTaskNotice = notice;
        if (status == "completed") RecordBoardCompletion(new(dailyTaskKey, dailyTaskTitle, dailyNow()), dailyWriter, dailyGoalContribution);
        dailyGoalContribution = null;
        if (activeDailyPlan?.CountWholePeriod != true) EndDailyRecord(status);
        UpdateDailyUi();
    }
    private void EndDailyRecord(string status)
    {
        var writer = dailyWriter;
        if (status == "completed" && dailyCreditClock?.RemainingSeconds > 0) status = "interrupted";
        dailyWriter = null; dailyCreditClock = null;
        if (writer == null) return;
        dailyEndTask = SaveDailyEndAsync(writer, status, dailyNow());
    }
    private async Task SaveDailyEndAsync(SessionWriter writer, string status, DateTimeOffset now)
    {
        try
        {
            await focusWork.RunAsync(() => { store.EndSession(writer.Id, writer.Failed ? "interrupted" : status, now); Interlocked.Increment(ref focusDataRevision); });
            RefreshStatistics();
        }
        catch (Exception ex) { if (DailyActive) StopDailyFocus("interrupted", "任务结束记录保存失败，全天限制已解除：" + ex.Message, true); else Status("结束状态保存失败，已保存时长保留：" + ex.Message); }
    }
    private void ForceExitDailyFocus() => StopDailyFocus("emergency", "已强制退出，今日计划已暂停；明天仍按计划执行。", true);
    private void StopDailyFocus(string status, string message, bool suppressToday)
    {
        if (!DailyActive && !dailyStarting) return;
        if (!dailyStarting)
            try { AdvanceDailyRecord(dailyNow()); }
            catch (Exception ex) { status = "interrupted"; message += " 末段记录未能保存：" + ex.Message; }
        var key = activeDailyWindow?.Key;
        if (status == "completed" && dailyTaskDeadline is { } deadline && dailyNow() >= deadline)
            RecordBoardCompletion(new(dailyTaskKey, dailyTaskTitle, deadline), dailyWriter, dailyGoalContribution);
        dailyGoalContribution = null;
        ++dailyGeneration;
        workspaceStartCancellation?.Cancel();
        activeDailyWindow = null;
        dailyTaskDeadline = null; EndDailyRecord(status);
        activeDailyPlan = null; activeDailySlot = null;
        ReleaseWorkspace(); bridge.Publish(false, [], 0);
        dailyTaskNotice = message;
        SetActiveUi(false); dailyEditor.SetActive(false); UpdateDailyUi(); Status(message);
        if (suppressToday && key != null)
        {
            dailyPlan.SuppressedWindow = key;
            dailyPlanTask = SaveDailySuppressionAsync(dailyPlan.Snapshot());
        }
    }
    private async Task SaveDailySuppressionAsync(DailyFocusPlan plan)
    {
        try { await focusWork.RunAsync(() => dailyPlanStore.Save(plan)); }
        catch (Exception ex) { if (dailyPlan.SuppressedWindow == plan.SuppressedWindow) dailyPlan.Enabled = false; Status("限制已解除，但今日暂停状态未保存；重启后计划可能恢复，请检查存储：" + ex.Message); }
    }
    private async Task RingDailyAlarmAsync()
    {
        try { await dailyAlarm(); }
        catch { if (DailyActive) { dailyTaskNotice += " 声音播放不可用，请查看屏幕提醒。"; UpdateDailyUi(); } }
    }
    private string DailyCountdown => dailyTaskDeadline is { } until ? FormatTime(Math.Max(0, (int)Math.Ceiling((until - dailyNow()).TotalSeconds))) : "--:--";
    private string WorkspaceCountdown => DailyActive ? DailyCountdown : FormatTime(clock?.RemainingSeconds ?? 0);
    private void UpdateDailyUi()
    {
        if (dailyEditor == null) return;
        string message = dailyTaskDeadline != null ? "正在做：" + dailyTaskTitle + " · 剩余 " + DailyCountdown : dailyTaskNotice;
        dailyTaskPanel.Update(DailyActive && !dailyStarting && !dailyTaskStarting, dailyTaskDeadline != null, message);
        foreach (var backdrop in workspaceBackdrops)
        {
            if (!DailyActive) break;
            backdrop.UpdateTime(DailyCountdown);
            backdrop.UpdateDailyTask(DailyActive && !dailyStarting && !dailyTaskStarting, dailyTaskDeadline != null, message,
                $"全天专注至 {activeDailyWindow!.End:MM月dd日 HH:mm} · {activeDailySlot?.Label ?? (activeDailyPlan!.AllowUnplannedApps ? "自由时段" : "只允许专注星球")}");
        }
        if (DailyActive)
        {
            EmergencyButton.IsEnabled = false; EmergencyButton.Content = "Ctrl+Alt+Shift+F12 强制退出";
            FullscreenButton.Visibility = Visibility.Collapsed;
            dailyEditor.RuntimeText.Text = $"全天专注至 {activeDailyWindow!.End:HH:mm} · 当前：{activeDailySlot?.Label ?? "空白时段"}";
        }
        else dailyEditor.RuntimeText.Text = !dailyPlan.Enabled ? "每日计划未启用。" : !dailyHotkeyRegistered ? "强制退出组合键不可用，计划不会自动开始。" : dailyPlan.WindowAt(dailyNow()) is { } currentWindow && currentWindow.Key == dailyPlan.SuppressedWindow ? "今日已暂停。需要继续时点击“重新启用今天”。" : clock != null ? "等待当前番茄钟结束后接管。" : $"每天 {dailyPlan.Start} — {dailyPlan.End}，保持程序运行，到点自动开始。";
    }
    private IEnumerable<WorkspaceWindowChoice> DailyLaunchChoices(IReadOnlyList<FocusWorkspaceAppWindow> windows)
    {
        if (!DailyActive || activeDailySlot == null || activeDailyPlan == null) yield break;
        foreach (var name in activeDailySlot.AppNames())
            if (!windows.Any(w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase)) && activeDailyPlan.ApplicationPaths.TryGetValue(name, out var path))
                yield return new(0, AppDisplayName(name + ".exe") + " · 打开", path);
    }
    private void LaunchDailyApp(string path)
    {
        try
        {
            if (!CanLaunchDailyApp(path)) return;
            if (!File.Exists(path)) throw new FileNotFoundException("应用文件已移动，请退出全天模式后重新选择应用。", path);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        }
        catch (Exception ex) { dailyTaskNotice = "应用未能打开：" + ex.Message; UpdateDailyUi(); Status(dailyTaskNotice); }
    }
    private bool CanLaunchDailyApp(string path)
    {
        var now = dailyNow();
        if (activeDailyPlan == null || activeDailyWindow is not { } window || now < window.Start || now >= window.End) return false;
        var slot = activeDailyPlan.SlotAt(now, window);
        var name = AppRules.NormalizeAllowed(Path.GetFileName(path));
        return slot != null && slot.AppNames().Contains(name) && activeDailyPlan.ApplicationPaths.TryGetValue(name, out var saved)
            && string.Equals(path, saved, StringComparison.OrdinalIgnoreCase);
    }
}
