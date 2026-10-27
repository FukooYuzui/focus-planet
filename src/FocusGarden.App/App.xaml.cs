using System.IO;
using System.Windows;

namespace FocusGarden.App;
public partial class App : Application
{
    private Mutex? instance;
    public static string DataDirectory { get; private set; } = "";
    public static bool TestMode { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool checkInstallation = e.Args.Contains("--check-installation");
        TestMode = e.Args.Contains("--test-mode");
        var idx = Array.IndexOf(e.Args, "--data-dir");
        if (checkInstallation && (idx < 0 || idx + 1 >= e.Args.Length)) { Shutdown(2); return; }
        DataDirectory = idx >= 0 && idx + 1 < e.Args.Length ? Path.GetFullPath(e.Args[idx + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusGarden");
        instance = new Mutex(true, checkInstallation ? "Local\\FocusPlanet-Check-" + Environment.ProcessId : "Local\\FocusGarden-MVP", out bool created);
        if (!created) { MessageBox.Show("专注星球已经在运行。请切换到现有窗口。", "专注星球"); Shutdown(); return; }
        Directory.CreateDirectory(DataDirectory);
        DispatcherUnhandledException += (_, args) => { if (MainWindow is MainWindow w) w.ReleaseForError(); MessageBox.Show("发生错误，已解除应用限制。浏览器扩展将在同步时解除。\n" + args.Exception.Message, "专注星球"); args.Handled = true; };
        try
        {
            MainWindow = new MainWindow();
            if (checkInstallation)
            {
                var content = (FrameworkElement)MainWindow.Content;
                content.Measure(new Size(1280, 940)); content.Arrange(new Rect(0, 0, 1280, 940)); content.UpdateLayout();
                File.WriteAllText(Path.Combine(DataDirectory, "installation-check.json"), System.Text.Json.JsonSerializer.Serialize(new { Product = "专注星球", Version = "0.4.0", Ready = true, MainWindow.Title, Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription }));
                MainWindow.Close(); Shutdown(); return;
            }
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            if (checkInstallation) { File.WriteAllText(Path.Combine(DataDirectory, "installation-check-error.txt"), ex.ToString()); Shutdown(1); return; }
            MessageBox.Show("无法启动，请检查数据目录是否可写。\n" + ex.Message, "专注星球"); Shutdown();
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
