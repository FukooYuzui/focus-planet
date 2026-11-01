using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FocusGarden.Core;
using Microsoft.Win32;

namespace FocusGarden.App;

internal sealed class DailyFocusEditor : StackPanel
{
    internal readonly CheckBox Enabled = new() { Content = "每天按计划自动开启（程序需保持运行，可最小化）" };
    internal readonly TextBox StartTime = new() { Width = 85 };
    internal readonly TextBox EndTime = new() { Width = 85 };
    internal readonly CheckBox CountWhole = new() { Content = "将全天开启时间全部计入专注（未勾选时仅累计任务钟）" };
    internal readonly CheckBox AllowGaps = new() { Content = "空白时段允许所有应用（未勾选时只允许专注星球）" };
    internal readonly DataGrid Slots = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 160, MaxHeight = 350, RowHeaderWidth = 0 };
    internal readonly TextBlock StatusText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12) };
    internal readonly TextBlock RuntimeText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly StackPanel settingsPanel = new();
    private readonly Button save = new() { Content = "保存并应用计划" };
    private readonly Button resume = new() { Content = "重新启用今天" };
    private Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    private ObservableCollection<DailyFocusSlot> rows = [];
    internal DailyFocusEditor(Func<DailyFocusPlan, Task<string?>> apply, Func<Task<string?>> reenable)
    {
        Children.Add(new TextBlock { Text = "全天专注", FontSize = 25, FontWeight = FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "按时间切换允许应用；任务钟到时只提醒。强制退出：Ctrl + Alt + Shift + F12。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 20) });
        Children.Add(settingsPanel);
        settingsPanel.Children.Add(Enabled);
        var times = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        times.Children.Add(new TextBlock { Text = "每日开启", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }); times.Children.Add(StartTime);
        times.Children.Add(new TextBlock { Text = "结束", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 10, 0) }); times.Children.Add(EndTime);
        times.Children.Add(new TextBlock { Text = "24 小时制 HH:mm；支持跨午夜", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0) });
        settingsPanel.Children.Add(times); settingsPanel.Children.Add(CountWhole); settingsPanel.Children.Add(AllowGaps);
        settingsPanel.Children.Add(new TextBlock { Text = "应用时段（不能重叠；空的应用列表表示只允许专注星球）", Margin = new Thickness(0, 18, 0, 8) });
        foreach (var (header, property, width) in new[] { ("开始", "Start", 90d), ("结束", "End", 90d), ("事项", "Label", 120d), ("允许的应用（多个用分号分隔）", "Apps", 300d) })
        {
            var textStyle = new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("Text")));
            textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(6, 4, 6, 4)));
            Slots.Columns.Add(new DataGridTextColumn { Header = header, MinWidth = property == "Apps" ? 220 : width, ElementStyle = textStyle,
                Binding = new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = property == "Apps" ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
        }
        Slots.Foreground = (Brush)FindResource("Text"); Slots.Background = new SolidColorBrush(Color.FromRgb(17, 30, 48));
        Slots.RowBackground = new SolidColorBrush(Color.FromRgb(27, 43, 67)); Slots.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, (Brush)FindResource("Text")));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 60, 88))));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 8, 6, 8)));
        Slots.ColumnHeaderStyle = headerStyle;
        settingsPanel.Children.Add(Slots);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Add(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => action(); buttons.Children.Add(b); return b; }
        Add("增加时段", () => { var row = new DailyFocusSlot { Start = StartTime.Text, End = EndTime.Text }; rows.Add(row); Slots.SelectedItem = row; });
        Add("删除选中时段", () => { if (Slots.SelectedItem is DailyFocusSlot row) rows.Remove(row); });
        Add("给选中时段选择应用文件", PickApp);
        settingsPanel.Children.Add(buttons);
        settingsPanel.Children.Add(new TextBlock { Text = "选择 EXE 会保存启动位置，届时可从浮动面板打开；也可填写 notepad.exe、WINWORD.exe 等进程名。仅填写名称的应用需事先打开。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 12) });
        settingsPanel.Children.Add(save); Children.Add(resume); Children.Add(StatusText); Children.Add(RuntimeText);
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                Slots.CommitEdit(DataGridEditingUnit.Cell, true); Slots.CommitEdit(DataGridEditingUnit.Row, true);
                var plan = new DailyFocusPlan { Enabled = Enabled.IsChecked == true, Start = StartTime.Text.Trim(), End = EndTime.Text.Trim(),
                    CountWholePeriod = CountWhole.IsChecked == true, AllowUnplannedApps = AllowGaps.IsChecked == true, Slots = rows.ToList(), ApplicationPaths = paths }.Snapshot();
                StatusText.Text = await apply(plan) ?? "计划已保存。";
            }
            catch (Exception error) { StatusText.Text = error.Message; }
            finally { save.IsEnabled = true; }
        };
        resume.Click += async (_, _) => { try { StatusText.Text = await reenable() ?? "今天的计划已重新启用。"; } catch (Exception e) { StatusText.Text = e.Message; } };
    }
    internal void Load(DailyFocusPlan plan)
    {
        Enabled.IsChecked = plan.Enabled; StartTime.Text = plan.Start; EndTime.Text = plan.End;
        CountWhole.IsChecked = plan.CountWholePeriod; AllowGaps.IsChecked = plan.AllowUnplannedApps;
        paths = new(plan.ApplicationPaths, StringComparer.OrdinalIgnoreCase);
        rows = new(plan.Slots.Select(s => new DailyFocusSlot { Start = s.Start, End = s.End, Label = s.Label, Apps = s.Apps })); Slots.ItemsSource = rows;
    }
    internal void SetActive(bool active) { settingsPanel.IsEnabled = !active; resume.IsEnabled = !active; }
    private void PickApp()
    {
        if (Slots.SelectedItem is not DailyFocusSlot slot) { StatusText.Text = "请先选中一个时段。"; return; }
        var dialog = new OpenFileDialog { Filter = "Windows 应用|*.exe", CheckFileExists = true, Title = "选择此时段允许的应用" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var name = AppRules.NormalizeAllowed(Path.GetFileName(dialog.FileName));
            paths[name] = dialog.FileName;
            slot.Apps = string.Join("; ", slot.AppNames().Append(name).Distinct(StringComparer.OrdinalIgnoreCase)); Slots.Items.Refresh();
            StatusText.Text = "已加入 " + name + "，保存计划后生效。";
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
}
