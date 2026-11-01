using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusGarden.Core;

namespace FocusGarden.App;

public partial class MainWindow
{
    private FocusBoardStore? boardStore;
    private FocusBoardSnapshot boardSnapshot = new("", []);
    private readonly TextBox boardPlansInput = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 180, MaxHeight = 330, MaxLength = FocusBoardStore.MaximumPlansLength };
    private readonly TextBlock boardStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) };
    private readonly StackPanel boardCompleted = new();
    private readonly DispatcherTimer boardDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Task boardLoadTask = Task.CompletedTask, boardTask = Task.CompletedTask;
    private bool boardReady, boardPlansDirty, boardLoadingText;
    private long boardEditRevision;
    private bool CanEditBoard => boardReady && !shuttingDown && clock == null && !startingFocus && !DailyActive && !dailyStarting && !dailyTaskStarting;
    private void InitializeFocusBoard()
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = "长期计划", FontSize = 23, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = "每行一项，停笔后自动保存。专注时左上角只显示计划，结束专注后才能编辑。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) });
        page.Children.Add(boardPlansInput); page.Children.Add(boardStatus);
        page.Children.Add(new TextBlock { Text = "最近完成", FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = "自动保留最近 100 项。提前退出、休息不会加入；历史专注统计不受此列表容量影响。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });
        page.Children.Add(boardCompleted); FocusBoardHost.Content = page;
        boardPlansInput.IsReadOnly = true;
        boardPlansInput.TextChanged += (_, _) =>
        {
            if (boardLoadingText || !CanEditBoard) return;
            boardPlansDirty = true; boardEditRevision++;
            boardStatus.Text = "尚未保存…"; boardDebounce.Stop(); boardDebounce.Start();
        };
        boardDebounce.Tick += async (_, _) => { boardDebounce.Stop(); await SaveBoardPlansAsync(); };
        boardLoadTask = LoadFocusBoardAsync();
    }
    private async Task LoadFocusBoardAsync()
    {
        try
        {
            boardSnapshot = await focusWork.RunAsync(() =>
            {
                boardStore = new FocusBoardStore(App.DataDirectory, store.GetCompletedSessions());
                return boardStore.Snapshot();
            });
            boardReady = true;
            boardLoadingText = true; boardPlansInput.Text = boardSnapshot.Plans; boardLoadingText = false;
            await ObserveBoardWriteAsync(focusWork.RunAsync(() => { boardStore!.Flush(); return boardStore.Snapshot(); }));
            UpdateBoardUi();
        }
        catch (Exception ex) { boardStatus.Text = "清单未加载，原文件已保留：" + ex.Message; }
    }
    private async Task<bool> SaveBoardPlansAsync()
    {
        boardDebounce.Stop();
        if (!boardReady || !boardPlansDirty) return true;
        var revision = boardEditRevision; var text = boardPlansInput.Text;
        bool saved = false;
        boardTask = SaveAsync(); await boardTask; return saved;
        async Task SaveAsync()
        {
            saved = await ObserveBoardWriteAsync(focusWork.RunAsync(() => { boardStore!.SetPlans(text); boardStore.Flush(); return boardStore.Snapshot(); }));
            if (saved && revision == boardEditRevision) boardPlansDirty = false;
            if (revision != boardEditRevision) boardStatus.Text = "尚未保存…";
        }
    }
    private async Task<bool> ObserveBoardWriteAsync(Task<FocusBoardSnapshot> work)
    {
        try { boardSnapshot = await work; boardStatus.Text = "已保存到本机 · 专注时仅供查看"; UpdateBoardUi(); return true; }
        catch (Exception ex) { boardStatus.Text = "清单尚未保存，可稍后重试；已保存的专注时长不受影响：" + ex.Message; return false; }
    }
    private void RecordBoardCompletion(CompletedFocusTask completion, SessionWriter? writer = null)
    {
        if (!boardReady) return;
        boardTask = ObserveBoardWriteAsync(focusWork.RunAsync(() =>
        {
            if (writer?.Failed != true) boardStore!.AddCompleted(completion);
            boardStore!.Flush(); return boardStore.Snapshot();
        }));
    }
    private async Task FlushFocusBoardAsync()
    {
        await boardLoadTask;
        await boardTask;
        if (!await SaveBoardPlansAsync()) throw new IOException("长期计划尚未保存。");
        if (boardReady)
        {
            boardSnapshot = await focusWork.RunAsync(() => { boardStore!.Flush(); return boardStore.Snapshot(); });
            UpdateBoardUi();
        }
    }
    private void ShowFocusBoard(object sender, RoutedEventArgs e) { Page("board"); UpdateBoardUi(); }
    private void UpdateBoardUi()
    {
        boardPlansInput.IsReadOnly = !CanEditBoard;
        immersion?.UpdateBoard(boardSnapshot);
        foreach (var backdrop in workspaceBackdrops) backdrop.UpdateBoard(boardSnapshot);
        // This method is called on edits/completions and mode transitions, never on timer ticks.
        boardCompleted.Children.Clear();
        if (boardSnapshot.Completed.Count == 0) boardCompleted.Children.Add(new TextBlock { Text = "完成一项任务后，会自动显示在这里。" });
        foreach (var item in boardSnapshot.Completed)
            boardCompleted.Children.Add(new TextBlock { Text = "✓ " + item.Title + "   ·   " + item.CompletedAt.ToLocalTime().ToString("MM-dd HH:mm"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
    }
}
