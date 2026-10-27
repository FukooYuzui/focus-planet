using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace FocusGarden.App;
internal sealed class ImmersionWindow : Window
{
    private readonly TextBlock countdown = new() { FontFamily = new FontFamily("Consolas"), FontSize = 150, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 20) };
    private bool allowClose;
    public ImmersionWindow(string task, bool hotkeyAvailable, Action emergency)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "专注花园 · 全屏沉浸"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = true; ShowInTaskbar = false;
        Loaded += (_, _) => WindowState = WindowState.Maximized;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 900, Margin = new Thickness(40) };
        stack.Children.Add(new TextBlock { Text = "F O C U S   G A R D E N", FontSize = 16, Foreground = (Brush)FindResource("Green"), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(countdown);
        stack.Children.Add(new TextBlock { Text = task, FontSize = 28, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "此刻，只做这一件事。", FontSize = 15, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 50) });
        var button = new Button { Content = "紧急退出", HorizontalAlignment = HorizontalAlignment.Center }; button.Click += (_, _) => emergency(); stack.Children.Add(button);
        stack.Children.Add(new TextBlock { Text = hotkeyAvailable ? "Ctrl + Alt + Esc 可立即解除；Esc 打开退出确认" : "快捷键被占用，请使用上方退出按钮；Esc 打开退出确认", HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 18, 0, 0), FontSize = 12 });
        Content = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = stack, Margin = new Thickness(24) };
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; Dispatcher.BeginInvoke(emergency); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; emergency(); } };
    }
    public void UpdateTime(string time) => countdown.Text = time;
    public void Release() { allowClose = true; Close(); }
}
