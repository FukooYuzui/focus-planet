using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Globalization;

namespace FocusGarden.App;
internal sealed class ImmersionWindow : Window
{
    private static readonly Brush LightText = new SolidColorBrush(Color.FromRgb(246, 250, 248));
    private static readonly Brush SecondaryText = new SolidColorBrush(Color.FromRgb(219, 232, 227));
    private readonly TextBlock countdown = new() { Foreground = LightText, FontFamily = new FontFamily("Consolas"), FontSize = 150, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 16) };
    private readonly TextBlock currentTime = new() { Foreground = SecondaryText, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 24) };
    private bool allowClose;
    public ImmersionWindow(string task, bool hotkeyAvailable, ImageSource? background, Action emergency)
    {
        Style = (Style)FindResource(typeof(Window));
        Background = new SolidColorBrush(Color.FromRgb(13, 20, 24));
        Foreground = LightText;
        Title = "专注星球 · 全屏沉浸"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = true; ShowInTaskbar = false;
        Loaded += (_, _) => WindowState = WindowState.Maximized;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 900, Margin = new Thickness(40) };
        stack.Children.Add(new TextBlock { Text = "F O C U S   G A R D E N", FontSize = 16, Foreground = (Brush)FindResource("Green"), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(currentTime);
        stack.Children.Add(new TextBlock { Text = "本轮剩余", FontSize = 14, Foreground = SecondaryText, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(countdown);
        stack.Children.Add(new TextBlock { Text = task, Foreground = LightText, FontSize = 28, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = "此刻，只做这一件事。", FontSize = 15, Foreground = SecondaryText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 32) });
        var button = new Button { Content = "紧急退出", Foreground = LightText, Background = new SolidColorBrush(Color.FromArgb(235, 37, 53, 60)), HorizontalAlignment = HorizontalAlignment.Center }; button.Click += (_, _) => emergency(); stack.Children.Add(button);
        stack.Children.Add(new TextBlock { Text = hotkeyAvailable ? "Ctrl + Alt + Esc 可立即解除；Esc 打开退出确认" : "快捷键被占用，请使用上方退出按钮；Esc 打开退出确认", HorizontalAlignment = HorizontalAlignment.Center, Foreground = SecondaryText, Margin = new Thickness(0, 18, 0, 0), FontSize = 12 });
        var surface = new Grid();
        if (background != null)
        {
            surface.Children.Add(new Image { Source = background, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            surface.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(174, 5, 12, 16)), IsHitTestVisible = false });
        }
        surface.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = stack, Margin = new Thickness(24) });
        Content = surface;
        UpdateCurrentTime();
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; Dispatcher.BeginInvoke(emergency); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; emergency(); } };
    }
    public void UpdateTime(string time) { countdown.Text = time; UpdateCurrentTime(); }
    private void UpdateCurrentTime() => currentTime.Text = "现在  " + DateTime.Now.ToString("yyyy年M月d日 dddd  HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"));
    public void Release() { allowClose = true; Close(); }
}
