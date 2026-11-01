using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed class GoalWorkPicker : StackPanel
{
    internal readonly ComboBox GoalChoice = new() { MinWidth = 230, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly TextBox PercentInput = new() { Text = "10", Width = 70, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel details = new();
    private readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 6) };
    private readonly List<CheckBox> checks = [];
    private string signature = "";
    private bool refreshing;
    internal GoalWorkPicker()
    {
        Margin = new Thickness(0, 10, 0, 10);
        Children.Add(new TextBlock { Text = "关联长期任务（可选）", FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        Children.Add(GoalChoice); Children.Add(details);
        GoalChoice.SelectionChanged += (_, _) => { if (!refreshing) ShowDetails(); };
        GoalChoice.ItemsSource = new object[] { "不关联长期任务" }; GoalChoice.SelectedIndex = 0;
    }
    internal string? SelectedTitle => (GoalChoice.SelectedItem as LongTermGoal)?.Title;
    internal void UpdateGoals(IReadOnlyList<LongTermGoal> goals)
    {
        var available = goals.Where(g => !g.Archived && g.Progress < 100).ToArray();
        var next = System.Text.Json.JsonSerializer.Serialize(available);
        if (next == signature) return;
        signature = next;
        var chosen = GoalChoice.SelectedItem as LongTermGoal;
        var ids = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet();
        refreshing = true;
        GoalChoice.ItemsSource = new object[] { "不关联长期任务" }.Concat(available).ToArray();
        GoalChoice.SelectedItem = available.FirstOrDefault(x => x.Id == chosen?.Id);
        if (GoalChoice.SelectedIndex < 0) GoalChoice.SelectedIndex = 0;
        refreshing = false; ShowDetails();
        foreach (var check in checks) check.IsChecked = ids.Contains((string)check.Tag);
    }
    private void ShowDetails()
    {
        if (PercentInput.Parent is Panel previousRow) previousRow.Children.Remove(PercentInput);
        details.Children.Clear(); checks.Clear();
        if (GoalChoice.SelectedItem is not LongTermGoal goal) return;
        hint.Text = "已完成 " + goal.ProgressLabel; details.Children.Add(hint);
        if (goal.Mode == "parts")
        {
            var items = new StackPanel();
            foreach (var part in goal.Parts.Where(p => !p.Completed))
            {
                var check = new CheckBox { Content = part.Title, Tag = part.Id, FontSize = 12, Margin = new Thickness(0, 3, 0, 3) };
                checks.Add(check); items.Children.Add(check);
            }
            details.Children.Add(new ScrollViewer { Content = items, MaxHeight = 105, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        else
        {
            PercentInput.Text = Math.Min(10, 100 - goal.Percent).ToString("0.##", CultureInfo.InvariantCulture);
            var row = new WrapPanel(); row.Children.Add(new TextBlock { Text = "本轮新增", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(PercentInput); row.Children.Add(new TextBlock { Text = $"%（最多 {100 - goal.Percent:0.##}%）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            details.Children.Add(row);
        }
    }
    internal GoalContribution? Capture()
    {
        if (GoalChoice.SelectedItem is not LongTermGoal goal) return null;
        if (goal.Mode == "parts")
        {
            var ids = checks.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray();
            if (ids.Length == 0) throw new ArgumentException("请勾选本轮要完成的部分。");
            return new(goal.Id, ids);
        }
        if (!decimal.TryParse(PercentInput.Text.Trim().TrimEnd('%', '％'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent) ||
            percent <= 0 || percent > 100 - goal.Percent || percent * 100 != decimal.Truncate(percent * 100))
            throw new ArgumentException($"本轮请输入 0 到 {100 - goal.Percent:0.##}% 之间的增量，最多两位小数，不含 0。");
        return new(goal.Id, [], percent);
    }
}
