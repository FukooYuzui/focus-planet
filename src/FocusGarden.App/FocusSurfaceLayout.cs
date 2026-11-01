using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusGarden.Core;

namespace FocusGarden.App;

/// <summary>Read-only corner cards, with a reserved center so small displays do not overlap.</summary>
internal sealed class FocusSurfaceLayout : Grid
{
    private readonly Border plansCard, completedCard;
    private readonly FrameworkElement center;
    private readonly TextBlock plans = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly StackPanel completed = new();
    private FocusBoardSnapshot? rendered;
    internal FocusSurfaceLayout(FrameworkElement centerContent)
    {
        Margin = new Thickness(20); center = centerContent;
        System.Windows.Documents.TextElement.SetForeground(this, Brushes.WhiteSmoke);
        plansCard = Card("长期计划", plans);
        completedCard = Card("最近完成", completed);
        plansCard.HorizontalAlignment = HorizontalAlignment.Left; plansCard.VerticalAlignment = VerticalAlignment.Top;
        completedCard.HorizontalAlignment = HorizontalAlignment.Right; completedCard.VerticalAlignment = VerticalAlignment.Bottom;
        Children.Add(center); Children.Add(plansCard); Children.Add(completedCard);
        SizeChanged += (_, _) => ArrangeCards();
        Update(new("", [])); ArrangeCards();
    }
    private static Border Card(string title, UIElement body)
    {
        var panel = new DockPanel();
        var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(167, 222, 190)), Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        panel.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return new Border { Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(225, 18, 31, 43)), BorderBrush = new SolidColorBrush(Color.FromArgb(180, 61, 85, 94)), BorderThickness = new Thickness(1) };
    }
    internal void Update(FocusBoardSnapshot snapshot)
    {
        if (rendered != null && rendered.Plans == snapshot.Plans && rendered.Completed.SequenceEqual(snapshot.Completed)) return;
        rendered = snapshot;
        plans.Text = string.IsNullOrWhiteSpace(snapshot.Plans) ? "暂无长期计划\n可在专注外的“计划清单”页填写。" :
            string.Join("\n\n", snapshot.Plans.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => "• " + x));
        completed.Children.Clear();
        if (snapshot.Completed.Count == 0) completed.Children.Add(new TextBlock { Text = "完成番茄钟或任务钟后，\n会自动显示在这里。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        foreach (var task in snapshot.Completed)
        {
            var text = new TextBlock { Text = "✓ " + task.Title + "\n" + task.CompletedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), ToolTip = task.Title };
            completed.Children.Add(text);
        }
    }
    private void ArrangeCards()
    {
        bool narrow = ActualWidth < 1050;
        RowDefinitions.Clear(); ColumnDefinitions.Clear();
        if (narrow)
        {
            RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
            SetRow(plansCard, 0); SetRow(center, 1); SetRow(completedCard, 2);
            foreach (UIElement child in Children) SetColumn(child, 0);
        }
        else
        {
            ColumnDefinitions.Add(new() { Width = new GridLength(240) }); ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); ColumnDefinitions.Add(new() { Width = new GridLength(240) });
            SetColumn(plansCard, 0); SetColumn(center, 1); SetColumn(completedCard, 2);
            foreach (UIElement child in Children) SetRow(child, 0);
        }
        plansCard.Width = completedCard.Width = Math.Max(120, Math.Min(narrow ? 290 : 230, ActualWidth));
        plansCard.MaxHeight = completedCard.MaxHeight = Math.Max(70, Math.Min(narrow ? 160 : 270, ActualHeight * (narrow ? .23 : .45)));
    }
}
