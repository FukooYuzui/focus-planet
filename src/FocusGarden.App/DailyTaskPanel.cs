using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed class DailyTaskPanel : StackPanel
{
    internal TextBox TaskInput { get; } = new() { MaxLength = 200, MinWidth = 220, Text = "" };
    internal TextBox MinutesInput { get; } = new() { Width = 65, Text = "25" };
    internal GoalWorkPicker GoalPicker { get; } = new();
    internal Button StartButton { get; } = new() { Content = "开始任务钟" };
    internal Button StopButton { get; } = new() { Content = "停止任务钟", Visibility = Visibility.Collapsed };
    internal TextBlock StateText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) };
    private bool submitting;
    private bool available;
    private string? lastRuntimeMessage;
    internal DailyTaskPanel(Func<string, int, GoalContribution?, Task<string?>> start, Action stop)
    {
        System.Windows.Documents.TextElement.SetForeground(this, (Brush)FindResource("Text"));
        MaxWidth = 680;
        Children.Add(new TextBlock { Text = "接下来做什么？", FontSize = 18, Margin = new Thickness(0, 0, 0, 10) });
        Children.Add(TaskInput);
        Children.Add(GoalPicker);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(MinutesInput);
        row.Children.Add(new TextBlock { Text = "分钟", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) });
        row.Children.Add(StartButton); row.Children.Add(StopButton); Children.Add(row); Children.Add(StateText);
        Children.Add(new TextBlock { Text = "到时响铃提醒，全天专注与应用限制继续。", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)FindResource("Muted") });
        StartButton.Click += async (_, _) =>
        {
            if (submitting) return;
            if (!int.TryParse(MinutesInput.Text, out var minutes)) { StateText.Text = "请输入 1–720 的整数分钟。"; return; }
            submitting = true; StartButton.IsEnabled = false;
            try { if (await start(TaskInput.Text, minutes, GoalPicker.Capture()) is { } error) StateText.Text = error; }
            catch (Exception error) { StateText.Text = error.Message; }
            finally { submitting = false; StartButton.IsEnabled = available; }
        };
        StopButton.Click += (_, _) => stop();
    }
    internal void Update(bool enabled, bool running, string message)
    {
        available = enabled;
        TaskInput.IsEnabled = MinutesInput.IsEnabled = enabled && !running && !submitting;
        GoalPicker.IsEnabled = enabled && !running && !submitting;
        StartButton.IsEnabled = enabled && !submitting;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (lastRuntimeMessage != message) { lastRuntimeMessage = message; StateText.Text = message; }
    }
}
