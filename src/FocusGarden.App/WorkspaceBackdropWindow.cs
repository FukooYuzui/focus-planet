using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FocusGarden.App;

/// <summary>
/// A lightweight focus surface for each display. It has no owner and does not
/// become topmost: the workspace guard keeps allowed application windows above it.
/// </summary>
internal sealed class WorkspaceBackdropWindow : Window
{
    private static readonly Brush LightText = FrozenBrush(Color.FromRgb(246, 250, 248));
    private static readonly Brush SecondaryText = FrozenBrush(Color.FromRgb(219, 232, 227));
    private readonly TextBlock countdown = new()
    {
        Name = "WorkspaceCountdown", Foreground = LightText, FontFamily = new FontFamily("Consolas"),
        FontSize = 138, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 16)
    };
    private readonly TextBlock currentTime = new()
    {
        Name = "WorkspaceCurrentTime", Foreground = SecondaryText, FontSize = 18,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 30)
    };
    private MonitorArea? monitor;
    private bool allowClose;
    private StackPanel focusContent = null!;
    private DailyTaskPanel? dailyTaskPanel;
    private TextBlock? dailyHeader;
    private bool watchingDisplays;
    internal event EventHandler? DisplayConfigurationChanged;

    internal WorkspaceBackdropWindow(string task, ImageSource? background, Action emergency, bool strict)
    {
        ArgumentNullException.ThrowIfNull(emergency);
        Style = (Style)FindResource(typeof(Window));
        Title = "专注星球 · 白名单专注界面";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Width = 1000;
        Height = 720;
        Background = FrozenBrush(Color.FromRgb(13, 20, 29));
        Foreground = LightText;

        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 900, Margin = new Thickness(32)
        };
        focusContent = content;
        content.Children.Add(new TextBlock
        {
            Text = "专 注 星 球", FontSize = 18, Foreground = (Brush)FindResource("Green"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(currentTime);
        content.Children.Add(new TextBlock
        {
            Text = "白名单专注 · 本轮剩余", FontSize = 16, Foreground = SecondaryText,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(countdown);
        content.Children.Add(new TextBlock
        {
            Name = "WorkspaceTask", Text = task, Foreground = LightText, FontSize = 28,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "从浮动面板切换允许的应用", FontSize = 16, Foreground = SecondaryText,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 32)
        });
        var exit = new Button
        {
            Name = "EmergencyExitButton", Content = strict ? "提前退出已禁用" : "紧急退出",
            IsEnabled = !strict, Foreground = LightText, Background = FrozenBrush(Color.FromRgb(37, 53, 65)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0)
        };
        exit.Click += (_, _) => { if (!strict) emergency(); };
        content.Children.Add(exit);
        content.Children.Add(new TextBlock
        {
            Name = "ExitHint",
            Text = strict ? "严格专注中 · 倒计时结束后恢复桌面" : "Esc 打开退出确认 · 结束本轮后恢复桌面",
            Foreground = SecondaryText, FontSize = 12, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = "系统恢复：Ctrl + Alt + Del / Ctrl + Shift + Esc / Win + L",
            Foreground = SecondaryText, FontSize = 11, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        });

        var surface = new Grid();
        if (background != null)
        {
            surface.Children.Add(new Image { Source = background, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            surface.Children.Add(new Border { Background = FrozenBrush(Color.FromArgb(185, 5, 12, 20)), IsHitTestVisible = false });
        }
        surface.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            Child = content, Margin = new Thickness(24)
        });
        Content = surface;
        UpdateTime("00:00");

        // Native positioning uses the same coordinate space as GetMonitorInfo.
        // Reapply after WPF's initial layout so the full monitor, including the
        // taskbar area, remains covered on mixed-scale displays. A later display
        // change ends this lease instead of continuing with stale monitor bounds.
        SourceInitialized += (_, _) => ApplyMonitorBounds();
        Loaded += (_, _) =>
        {
            ApplyMonitorBounds();
            if (allowClose || watchingDisplays) return;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            watchingDisplays = true;
        };
        DpiChanged += (_, _) => { if (watchingDisplays) QueueDisplayConfigurationChanged(); };
        Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (!strict) Dispatcher.BeginInvoke(emergency);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (!strict) emergency();
        };
    }

    internal static IReadOnlyList<WorkspaceBackdropWindow> CreateForDisplays(
        string task, ImageSource? background, Action emergency, bool strict)
    {
        var areas = new List<MonitorArea>();
        var failed = false;
        if (!EnumDisplayMonitors(0, 0, (nint display, nint _, ref NativeRect bounds, nint state) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(display, ref info)) { failed = true; return false; }
                if (info.Monitor.Right > info.Monitor.Left && info.Monitor.Bottom > info.Monitor.Top)
                    areas.Add(new(info.Monitor, (info.Flags & 1) != 0));
                return true;
            }, 0) || failed || areas.Count == 0)
            throw new InvalidOperationException("无法读取显示器范围，白名单专注界面未能启动。");

        // Primary first gives the guard a predictable target when returning from desktop.
        var windows = new List<WorkspaceBackdropWindow>();
        try
        {
            foreach (var area in areas.OrderByDescending(x => x.Primary).ThenBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top))
                windows.Add(new WorkspaceBackdropWindow(task, background, emergency, strict) { monitor = area });
            return windows;
        }
        catch
        {
            foreach (var window in windows) window.Release();
            throw;
        }
    }

    internal void UpdateTime(string time)
    {
        if (countdown.Text != time) countdown.Text = time;
        var text = "现在  " + DateTime.Now.ToString("yyyy年M月d日 dddd  HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"));
        if (currentTime.Text != text) currentTime.Text = text;
    }

    internal void ConfigureDaily(Func<string, int, Task<string?>> start, Action stop)
    {
        countdown.FontSize = 70;
        dailyHeader = new TextBlock { Foreground = LightText, FontSize = 16, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 12) };
        dailyTaskPanel = new DailyTaskPanel(start, stop) { Margin = new Thickness(0, 12, 0, 0) };
        focusContent.Children.Clear();
        focusContent.Children.Add(currentTime); focusContent.Children.Add(dailyHeader); focusContent.Children.Add(countdown);
        focusContent.Children.Add(dailyTaskPanel);
        focusContent.Children.Add(new TextBlock { Foreground = SecondaryText, Text = "强制退出全天模式：Ctrl + Alt + Shift + F12", TextAlignment = TextAlignment.Center, FontSize = 13, Margin = new Thickness(0, 20, 0, 0) });
        focusContent.Children.Add(new TextBlock { Foreground = SecondaryText, Text = "系统恢复：Ctrl + Alt + Del / Ctrl + Shift + Esc / Win + L", TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
    }
    internal void UpdateDailyTask(bool enabled, bool running, string message, string header)
    {
        dailyTaskPanel?.Update(enabled, running, message);
        if (dailyHeader != null && dailyHeader.Text != header) dailyHeader.Text = header;
    }

    internal void Release()
    {
        if (allowClose) return;
        allowClose = true;
        if (watchingDisplays)
        {
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            watchingDisplays = false;
        }
        Close();
    }

    // This method also lets hidden fixtures exercise the owner's release path
    // without registering a system event or changing real display settings.
    internal void NotifyDisplayConfigurationChanged()
    {
        if (!allowClose) DisplayConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => QueueDisplayConfigurationChanged();
    private void QueueDisplayConfigurationChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(NotifyDisplayConfigurationChanged));
    }

    private void ApplyMonitorBounds()
    {
        if (monitor is not { } area || allowClose) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var bounds = area.Bounds;
        if (!SetWindowPos(handle, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, 0x0004 | 0x0010)) // NOZORDER | NOACTIVATE
            throw new Win32Exception(Marshal.GetLastWin32Error(), "专注界面无法覆盖显示器。");
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record MonitorArea(NativeRect Bounds, bool Primary);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
