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
        TestMode = e.Args.Contains("--test-mode");
        var idx = Array.IndexOf(e.Args, "--data-dir");
        DataDirectory = idx >= 0 && idx + 1 < e.Args.Length ? Path.GetFullPath(e.Args[idx + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusGarden");
        instance = new Mutex(true, "Local\\FocusGarden-MVP", out bool created);
        if (!created) { MessageBox.Show("专注花园已经在运行。请切换到现有窗口。", "专注花园"); Shutdown(); return; }
        Directory.CreateDirectory(DataDirectory);
        DispatcherUnhandledException += (_, args) => { if (MainWindow is MainWindow w) w.ReleaseForError(); MessageBox.Show("发生错误，已解除应用限制。浏览器扩展将在同步时解除。\n" + args.Exception.Message, "专注花园"); args.Handled = true; };
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show("无法启动，请检查数据目录是否可写。\n" + ex.Message, "专注花园"); Shutdown(); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
