using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusGarden.Core;
using Microsoft.Win32;

namespace FocusGarden.App;

public partial class MainWindow
{
    private DiaryStore diaryStore = null!;
    private DiaryPreferences diaryPreferences = new();
    private readonly DatePicker diaryDate = new() { SelectedDate = DateTime.Today.AddDays(-1), DisplayDateEnd = DateTime.Today, Width = 150 };
    private readonly TextBox diaryEditor = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 470, MaxLength = DiaryStore.MaximumCharacters, FontFamily = new FontFamily("Consolas") };
    private readonly FlowDocumentScrollViewer diaryPreview = new() { Height = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock diaryStatus = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock diaryConnection = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox diaryAuto = new() { Content = "次日首次启动时，自动整理前一天", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox diaryCodex = new() { Content = "包含当天 Codex 对话节选", Margin = new Thickness(0, 8, 0, 8) };
    private readonly WrapPanel diaryActions = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button diaryCancel = new() { Content = "取消整理", IsEnabled = false };
    private readonly Button diaryDraft = new() { Content = "查看新草稿", IsEnabled = false };
    private readonly ComboBox diaryHistory = new() { Width = 160, Margin = new Thickness(12, 0, 0, 0) };
    private Task diaryLoadTask = Task.CompletedTask, diaryGenerationTask = Task.CompletedTask;
    private Func<string, string, CancellationToken, Task<string>> diaryGenerator = new CodexDiaryClient().GenerateAsync;
    private CancellationTokenSource? diaryCancellation;
    private DiaryEntry? diaryLoaded;
    private DateOnly diaryDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
    private string? diaryPendingDraft;
    private bool diaryReady, diaryLoadingText, diaryDirty, diaryBusy, diaryNavigating;
    private bool CanEditDiary => diaryReady && !shuttingDown && !diaryBusy && clock == null && !startingFocus && !DailyActive && !dailyStarting;

    private void InitializeDiary()
    {
        diaryStore = new DiaryStore(App.DataDirectory);
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = "工作日记", FontSize = 24, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = "记录专注、任务进展与 Codex 工作摘要。首次使用请展开设置，开启次日自动整理。", Margin = new Thickness(0, 10, 0, 12) });
        var settingsPanel = new StackPanel();
        settingsPanel.Children.Add(new TextBlock { Text = "整理会将所选工作记录发送给 Codex，使用当前登录和额度。无需在这里填写密钥。", Margin = new Thickness(0, 8, 0, 8) });
        settingsPanel.Children.Add(diaryAuto); settingsPanel.Children.Add(diaryCodex); settingsPanel.Children.Add(diaryConnection);
        var settingsRow = new WrapPanel();
        settingsRow.Children.Add(DiaryButton("保存日记设置", SaveDiarySettings));
        settingsRow.Children.Add(DiaryButton("选择 Codex 程序", ChooseDiaryCodex));
        settingsRow.Children.Add(DiaryButton("打开日记文件夹", (_, _) => { Directory.CreateDirectory(diaryStore.DirectoryPath); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { diaryStore.DirectoryPath }, UseShellExecute = false }); }));
        settingsPanel.Children.Add(settingsRow);
        settingsPanel.Children.Add(new TextBlock { Text = "只选取当天用户与助手消息，超长记录会节选。不会读取工具输出或登录文件。基础密钥遮盖不能识别所有隐私，可关闭对话来源。", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 16) });
        page.Children.Add(new Expander { Header = "自动整理与 Codex 设置", Content = settingsPanel, Margin = new Thickness(0, 0, 0, 16), Foreground = Brushes.WhiteSmoke });
        var dates = new WrapPanel(); dates.Children.Add(diaryDate); diaryHistory.ToolTip = "选择已有日记"; dates.Children.Add(diaryHistory); page.Children.Add(dates);
        diaryActions.Children.Add(DiaryButton("用 Codex 生成日记", async (_, _) => await GenerateDiaryAsync(diaryDay, false)));
        diaryActions.Children.Add(DiaryButton("创建本地草稿", async (_, _) => await CreateLocalDiaryAsync()));
        diaryActions.Children.Add(DiaryButton("保存 Markdown", async (_, _) => await SaveDiaryAsync()));
        diaryActions.Children.Add(DiaryButton("重新读取", async (_, _) =>
        {
            if (!CanEditDiary) return;
            if (diaryDirty && MessageBox.Show(this, "放弃尚未保存的文字，重新读取本机日记？", "重新读取", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await OpenDiaryAsync(diaryDay, true);
        }));
        diaryActions.Children.Add(diaryDraft); page.Children.Add(diaryActions); diaryCancel.Visibility = Visibility.Collapsed; diaryCancel.HorizontalAlignment = HorizontalAlignment.Left; page.Children.Add(diaryCancel); page.Children.Add(diaryStatus);
        var tabs = new TabControl { Background = new SolidColorBrush(Color.FromRgb(16, 26, 31)), BorderThickness = new Thickness(0) }; tabs.Items.Add(new TabItem { Header = "Markdown 编辑", Content = diaryEditor });
        tabs.Items.Add(new TabItem { Header = "阅读预览", Content = diaryPreview }); page.Children.Add(tabs);
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs && tabs.SelectedIndex == 1) diaryPreview.Document = DiaryMarkdown.Render(diaryEditor.Text); };
        DiaryHost.Content = page;
        diaryEditor.TextChanged += (_, _) => { if (!diaryLoadingText) { diaryDirty = true; diaryStatus.Text = "有未保存的修改，请点击保存 Markdown。"; } };
        diaryDate.SelectedDateChanged += async (_, _) =>
        {
            if (diaryNavigating) return;
            if (!CanEditDiary || diaryDirty) { diaryStatus.Text = "请先保存当前修改，或等待专注/整理结束后切换日期。"; diaryNavigating = true; diaryDate.SelectedDate = diaryDay.ToDateTime(TimeOnly.MinValue); diaryNavigating = false; return; }
            if (diaryDate.SelectedDate is { } day) await OpenDiaryAsync(DateOnly.FromDateTime(day));
        };
        diaryHistory.SelectionChanged += (_, _) => { if (diaryHistory.SelectedItem is DateOnly day) diaryDate.SelectedDate = day.ToDateTime(TimeOnly.MinValue); };
        diaryCancel.Click += (_, _) => diaryCancellation?.Cancel();
        diaryDraft.Click += (_, _) =>
        {
            if (!CanEditDiary || diaryDirty || diaryPendingDraft == null) { diaryStatus.Text = "请先保存当前修改。"; return; }
            diaryEditor.Text = diaryPendingDraft; diaryDirty = true; diaryStatus.Text = "已打开新草稿。点击保存才会替换正文，旧正文会保留备份。";
        };
        diaryLoadTask = LoadDiaryAsync();
        Loaded += async (_, _) =>
        {
            await diaryLoadTask; await boardLoadTask;
            if (!App.TestMode && !App.InstallationCheck && diaryReady && diaryPreferences.Automatic) await GenerateDiaryAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), true);
        };
    }
    private static Button DiaryButton(string title, RoutedEventHandler handler) { var button = new Button { Content = title, Margin = new Thickness(0, 0, 10, 8) }; button.Click += handler; return button; }
    private async Task LoadDiaryAsync()
    {
        try
        {
            diaryPreferences = await Task.Run(diaryStore.LoadPreferences);
            diaryAuto.IsChecked = diaryPreferences.Automatic; diaryCodex.IsChecked = diaryPreferences.IncludeCodex;
            diaryReady = true; await OpenDiaryAsync(diaryDay); RefreshDiaryConnection();
        }
        catch (Exception ex) { diaryStatus.Text = "日记未能加载，原文件已保留：" + ex.Message; }
        UpdateDiaryUi();
    }
    private async Task OpenDiaryAsync(DateOnly day, bool discard = false)
    {
        if ((diaryDirty && !discard) || diaryBusy) return;
        diaryBusy = true; UpdateDiaryUi();
        try
        {
            var loaded = await Task.Run(() => diaryStore.Read(day));
            diaryDay = day; diaryLoaded = loaded; diaryPendingDraft = null;
            diaryLoadingText = true; diaryEditor.Text = loaded?.Markdown ?? ""; diaryLoadingText = false; diaryDirty = false;
            diaryPreview.Document = DiaryMarkdown.Render(diaryEditor.Text);
            diaryHistory.ItemsSource = await Task.Run(diaryStore.ListDays);
            diaryStatus.Text = loaded == null ? "这一天还没有日记。可以生成日记或创建本地草稿。" : "已读取本机 Markdown · " + diaryStore.PathFor(day);
        }
        catch (Exception ex) { diaryStatus.Text = "读取失败，原日记保留：" + ex.Message; diaryNavigating = true; diaryDate.SelectedDate = diaryDay.ToDateTime(TimeOnly.MinValue); diaryNavigating = false; }
        finally { diaryBusy = false; UpdateDiaryUi(); }
    }
    private async Task<bool> SaveDiaryAsync()
    {
        if (!diaryDirty) return true;
        if (!diaryReady || diaryBusy) return false;
        var day = diaryDay; var text = diaryEditor.Text; var revision = diaryLoaded?.Revision;
        diaryBusy = true; UpdateDiaryUi();
        try { diaryLoaded = await Task.Run(() => diaryStore.Save(day, text, revision)); diaryDirty = false; diaryStatus.Text = "Markdown 已保存；替换前的正文已保留备份。"; diaryHistory.ItemsSource = await Task.Run(diaryStore.ListDays); return true; }
        catch (Exception ex) { diaryStatus.Text = "尚未保存，编辑内容仍在：" + ex.Message; return false; }
        finally { diaryBusy = false; UpdateDiaryUi(); }
    }
    private async Task<DiaryDayFacts> DiaryFactsAsync(DateOnly day)
    {
        await boardLoadTask;
        return await focusWork.RunAsync(() =>
        {
            var board = boardStore?.Snapshot() ?? new FocusBoardSnapshot("", []);
            return new DiaryDayFacts(day, store.GetDiaryFocus(day), board.Completed.Where(x => DateOnly.FromDateTime(x.CompletedAt.LocalDateTime) == day).ToArray(), board.DisplayPlans);
        });
    }
    private async Task CreateLocalDiaryAsync()
    {
        if (!CanEditDiary) return;
        if (diaryDirty || diaryLoaded != null) { diaryStatus.Text = "当前已有正文或修改，未覆盖。可使用 Codex 另存生成草稿。"; return; }
        diaryBusy = true; UpdateDiaryUi();
        try { diaryEditor.Text = DiarySource.LocalDraft(await DiaryFactsAsync(diaryDay)); }
        catch (Exception ex) { diaryStatus.Text = "创建失败：" + ex.Message; }
        finally { diaryBusy = false; UpdateDiaryUi(); }
    }
    private Task GenerateDiaryAsync(DateOnly day, bool automatic)
    {
        if (automatic ? !diaryReady || shuttingDown || diaryBusy : !CanEditDiary) return Task.CompletedTask;
        diaryGenerationTask = GenerateDiaryCoreAsync(day, automatic); return diaryGenerationTask;
    }
    private async Task GenerateDiaryCoreAsync(DateOnly day, bool automatic)
    {
        if (!automatic && !await SaveDiaryAsync()) return;
        diaryBusy = true; diaryCancellation = new(); diaryCancel.IsEnabled = true; diaryCancel.Visibility = Visibility.Visible; UpdateDiaryUi();
        var token = diaryCancellation.Token;
        try
        {
            if (automatic && await Task.Run(() => diaryStore.Read(day), token) != null) { diaryStatus.Text = "前一天已有日记，自动整理不会覆盖。"; return; }
            var executable = diaryPreferences.CodexExecutable ?? CodexDiaryClient.FindExecutable();
            if (executable == null) throw new IOException("未找到 Codex CLI。安装并登录后，选择 codex.exe；也可以先创建本地草稿。");
            diaryStatus.Text = "正在整理 " + day.ToString("yyyy-MM-dd") + " 的工作…可继续查看，或取消整理。";
            var facts = await DiaryFactsAsync(day);
            var home = dashboardPreferences.ResolveCodexHome();
            var excerpt = diaryPreferences.IncludeCodex ? await Task.Run(() => DiarySource.ReadCodex(home, day, token), token) : new CodexDiaryExcerpt("用户关闭了对话来源。", 0, false, 0);
            var prompt = DiarySource.BuildPrompt(facts, excerpt);
            var markdown = await diaryGenerator(executable, prompt, token);
            token.ThrowIfCancellationRequested();
            var path = await Task.Run(() => diaryStore.SaveGenerated(day, markdown), token);
            if (day == diaryDay && !diaryDirty)
            {
                if (path == diaryStore.PathFor(day)) { diaryLoaded = await Task.Run(() => diaryStore.Read(day)); diaryLoadingText = true; diaryEditor.Text = diaryLoaded!.Markdown; diaryLoadingText = false; }
                else diaryPendingDraft = markdown;
            }
            diaryHistory.ItemsSource = await Task.Run(diaryStore.ListDays);
            diaryPreview.Document = DiaryMarkdown.Render(diaryEditor.Text);
            diaryStatus.Text = path == diaryStore.PathFor(day) ? "日记已生成并保存：" + path : "新草稿已另存，原正文未覆盖。点击“查看新草稿”后可编辑并保存。\n" + path;
        }
        catch (OperationCanceledException) { diaryStatus.Text = "已取消整理，已有日记保留。"; }
        catch (Exception ex) { diaryStatus.Text = "整理未完成，可手动重试：" + ex.Message; }
        finally { diaryCancellation.Dispose(); diaryCancellation = null; diaryBusy = false; diaryCancel.IsEnabled = false; diaryCancel.Visibility = Visibility.Collapsed; UpdateDiaryUi(); }
    }
    private void SaveDiarySettings(object sender, RoutedEventArgs e)
    {
        if (!CanEditDiary) return;
        try { var candidate = diaryPreferences with { Automatic = diaryAuto.IsChecked == true, IncludeCodex = diaryCodex.IsChecked == true }; diaryStore.SavePreferences(candidate); diaryPreferences = candidate; diaryStatus.Text = "设置已保存。自动整理从下次启动生效，只整理前一天，已有正文不会覆盖。"; }
        catch (Exception ex) { diaryStatus.Text = "设置未保存：" + ex.Message; }
    }
    private void ChooseDiaryCodex(object sender, RoutedEventArgs e)
    {
        if (!CanEditDiary) return;
        var dialog = new OpenFileDialog { Title = "选择 Codex CLI 的 codex.exe", Filter = "Codex 程序|codex.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { var candidate = diaryPreferences with { CodexExecutable = dialog.FileName }; diaryStore.SavePreferences(candidate); diaryPreferences = candidate; RefreshDiaryConnection(); }
        catch (Exception ex) { diaryStatus.Text = "路径未保存：" + ex.Message; }
    }
    private void RefreshDiaryConnection()
    {
        var path = diaryPreferences.CodexExecutable ?? CodexDiaryClient.FindExecutable();
        diaryConnection.Text = path == null ? "未找到 Codex CLI，请安装并登录后选择程序。" : "已找到 Codex · 整理时使用独立只读会话";
        diaryConnection.ToolTip = path;
    }
    private void UpdateDiaryUi()
    {
        if (diaryStore == null) return;
        diaryEditor.IsReadOnly = !CanEditDiary; diaryActions.IsEnabled = CanEditDiary; diaryDate.IsEnabled = CanEditDiary; diaryHistory.IsEnabled = CanEditDiary;
        diaryAuto.IsEnabled = CanEditDiary; diaryCodex.IsEnabled = CanEditDiary; diaryDraft.IsEnabled = CanEditDiary && diaryPendingDraft != null;
    }
    private void ShowDiary(object sender, RoutedEventArgs e) { Page("diary"); UpdateDiaryUi(); }
    private async Task CloseDiaryAsync()
    {
        await diaryLoadTask; diaryCancellation?.Cancel(); await diaryGenerationTask;
        if (!await SaveDiaryAsync()) throw new IOException("日记修改尚未保存。");
    }
}
