using System.Windows;
using System.Windows.Controls;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed class LongGoalEditor : StackPanel
{
    private readonly ListBox goals = new() { MaxHeight = 180, MinHeight = 70 };
    private readonly TextBox title = new() { MaxLength = 120 };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "按百分比推进", "分成多个部分" }, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox count = new() { Text = "5", Width = 60 };
    private readonly TextBox parts = new() { AcceptsReturn = true, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 8100 };
    private readonly StackPanel partsPanel = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
    private string? selectedId;
    private bool refreshing;
    internal LongGoalEditor(Func<GoalDraft, Task<LongTermGoal>> save, Func<string, Task> archive)
    {
        goals.SetResourceReference(Control.ForegroundProperty, "Text");
        goals.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 31, 43));
        Children.Add(new TextBlock { Text = "可推进的长期任务", FontSize = 22, FontWeight = FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "设置任务和进度方式；开始计时前再选择本轮要完成的部分或百分比。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        Children.Add(goals);
        var fresh = new Button { Content = "＋ 新建任务", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 10) };
        fresh.Click += (_, _) => LoadGoal(null); Children.Add(fresh);
        Children.Add(new TextBlock { Text = "任务名称", Margin = new Thickness(0, 0, 0, 6) }); Children.Add(title);
        mode.Margin = new Thickness(0, 10, 0, 10); Children.Add(mode); Children.Add(partsPanel);
        var generate = new WrapPanel(); generate.Children.Add(new TextBlock { Text = "分成", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); generate.Children.Add(count);
        var button = new Button { Content = "生成部分", Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) =>
        {
            if (!int.TryParse(count.Text, out int number) || number is < 1 or > 100) { status.Text = "请输入 1–100 个部分。"; return; }
            if (goals.SelectedItem is LongTermGoal goal && goal.Parts.Any(x => x.Completed)) { status.Text = "已有完成部分，请直接编辑下方列表，保留已完成部分的名称。"; return; }
            parts.Text = string.Join(Environment.NewLine, Enumerable.Range(1, number).Select(i => "第 " + i + " 部分"));
        };
        generate.Children.Add(button); partsPanel.Children.Add(generate);
        partsPanel.Children.Add(new TextBlock { Text = "每行一个部分，可自由命名；完成后保留名称以保护进度。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 6) }); partsPanel.Children.Add(parts);
        mode.SelectionChanged += (_, _) => partsPanel.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        partsPanel.Visibility = Visibility.Collapsed;
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var saveButton = new Button { Content = "保存任务" }; var archiveButton = new Button { Content = "归档选中任务" }; actions.Children.Add(saveButton); actions.Children.Add(archiveButton); Children.Add(actions); Children.Add(status);
        saveButton.Click += async (_, _) =>
        {
            saveButton.IsEnabled = false;
            try { var result = await save(new(selectedId, title.Text, mode.SelectedIndex == 1 ? "parts" : "percent", parts.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))); LoadGoal(result); status.Text = "任务已保存，进度保持不变。"; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { saveButton.IsEnabled = true; }
        };
        archiveButton.Click += async (_, _) =>
        {
            if (selectedId == null) return;
            try { await archive(selectedId); LoadGoal(null); status.Text = "任务已归档，完成记录与进度保留。"; }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        goals.SelectionChanged += (_, _) => { if (!refreshing && goals.SelectedItem is LongTermGoal goal) LoadGoal(goal); };
    }
    internal void UpdateGoals(IReadOnlyList<LongTermGoal> values)
    {
        refreshing = true; goals.ItemsSource = values.Where(x => !x.Archived).ToArray();
        goals.SelectedItem = values.FirstOrDefault(x => x.Id == selectedId && !x.Archived); refreshing = false;
    }
    private void LoadGoal(LongTermGoal? goal)
    {
        selectedId = goal?.Id; title.Text = goal?.Title ?? ""; mode.SelectedIndex = goal?.Mode == "parts" ? 1 : 0;
        mode.IsEnabled = goal == null || goal.Progress == 0;
        parts.Text = goal == null ? "" : string.Join(Environment.NewLine, goal.Parts.Select(x => x.Title));
        status.Text = goal == null ? "填写后点击“保存任务”。" : "当前进度：" + goal.ProgressLabel;
        if (goal == null) { refreshing = true; goals.SelectedIndex = -1; refreshing = false; }
    }
}
