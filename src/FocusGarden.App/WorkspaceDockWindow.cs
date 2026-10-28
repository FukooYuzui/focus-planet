using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusGarden.App;

internal sealed record WorkspaceWindowChoice(nint Handle, string Label)
{
    public override string ToString() => Label;
}

internal sealed class WorkspaceDockWindow : Window
{
    private readonly TextBlock time = new() { FontFamily = new FontFamily("Consolas"), FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox apps = new() { MinWidth = 180, MaxWidth = 290, DisplayMemberPath = nameof(WorkspaceWindowChoice.Label) };
    private readonly TextBlock status = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(188, 202, 221)), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button switchApp;
    private WorkspaceWindowChoice[] rendered = [];
    private bool allowClose;

    internal WorkspaceDockWindow(Action<nint> activate, Action showMain, Action emergency, bool strict)
    {
        Title = "专注星球 · 白名单工作区";
        Style = (Style)FindResource(typeof(Window));
        time.Foreground = (Brush)FindResource("Text");
        Width = 490; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(16, 28, 42));
        var content = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 9), Cursor = Cursors.SizeAll };
        DockPanel.SetDock(time, Dock.Right); title.Children.Add(time);
        title.Children.Add(new TextBlock { Text = "白名单专注 · 拖动此处移动", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Green"), FontSize = 12 });
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        content.Children.Add(title);
        var actions = new DockPanel();
        var main = new Button { Content = "专注星球", Padding = new Thickness(10, 7, 10, 7), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        main.Click += (_, _) => showMain(); DockPanel.SetDock(main, Dock.Right); actions.Children.Add(main);
        switchApp = new Button { Content = "切换", Padding = new Thickness(10, 7, 10, 7), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        switchApp.Click += (_, _) => { if (apps.SelectedItem is WorkspaceWindowChoice choice) activate(choice.Handle); };
        DockPanel.SetDock(switchApp, Dock.Right); actions.Children.Add(switchApp); actions.Children.Add(apps);
        content.Children.Add(actions);
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var stop = new Button { Name = "WorkspaceExitButton", Content = strict ? "到时恢复退出" : "结束本轮", IsEnabled = !strict, Padding = new Thickness(10, 5, 10, 5), FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
        stop.Click += (_, _) => emergency(); DockPanel.SetDock(stop, Dock.Right); bottom.Children.Add(stop);
        bottom.Children.Add(new TextBlock { Text = "系统恢复：Ctrl+Alt+Del / Ctrl+Shift+Esc / Win+L", FontSize = 10, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(bottom); content.Children.Add(status);
        Content = new Border { Child = content, BorderBrush = new SolidColorBrush(Color.FromRgb(57, 82, 101)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 16); Top = workArea.Top + 12;
        Closing += (_, e) => { if (!allowClose) e.Cancel = true; };
    }
    internal void UpdateTime(string value) { if (time.Text != value) time.Text = value; }
    internal void SetWindows(WorkspaceWindowChoice[] windows)
    {
        if (!rendered.SequenceEqual(windows))
        {
            var selected = (apps.SelectedItem as WorkspaceWindowChoice)?.Handle;
            rendered = windows; apps.ItemsSource = windows;
            apps.SelectedItem = windows.FirstOrDefault(w => w.Handle == selected);
            if (apps.SelectedIndex < 0 && windows.Length > 0) apps.SelectedIndex = 0;
        }
        switchApp.IsEnabled = windows.Length > 0;
        status.Text = windows.Length == 0 ? "白名单应用已关闭，可返回专注星球；到期自动恢复桌面。" : $"可切换 {windows.Length} 个应用窗口 · 普通编辑快捷键可用";
    }
    internal void Release() { allowClose = true; Close(); }
}
