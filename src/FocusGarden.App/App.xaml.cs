using System.IO;
using System.Windows;
using System.Security.Cryptography;
using System.Text;
using FocusGarden.Core;

namespace FocusGarden.App;
public partial class App : Application
{
    private Mutex? instance;
    private Mutex? dataInstance;
    public static string CurrentVersion => typeof(App).Assembly.GetName().Version!.ToString(3);
    public static string DataDirectory { get; private set; } = "";
    public static bool TestMode { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool checkInstallation = e.Args.Contains("--check-installation");
        TestMode = e.Args.Contains("--test-mode");
        var idx = Array.IndexOf(e.Args, "--data-dir");
        if ((checkInstallation || TestMode) && (idx < 0 || idx + 1 >= e.Args.Length)) { Shutdown(2); return; }
        string dataIdentity;
        try
        {
            DataDirectory = idx >= 0 && idx + 1 < e.Args.Length ? Path.GetFullPath(e.Args[idx + 1]) : FocusDataProtection.DefaultDataDirectory;
            dataIdentity=DataDirectoryIdentity.Resolve(DataDirectory);
            if ((checkInstallation || TestMode) && dataIdentity==DataDirectoryIdentity.Resolve(FocusDataProtection.DefaultDataDirectory)) { Shutdown(2); return; }
        }
        catch(Exception ex){if(!checkInstallation)MessageBox.Show("无法使用数据目录："+ex.Message,"专注星球");Shutdown(1);return;}
        instance = new Mutex(true, checkInstallation ? "Local\\FocusPlanet-Check-" + Environment.ProcessId : "Local\\FocusGarden-MVP", out bool created);
        if (!created) { MessageBox.Show("专注星球已经在运行。请切换到现有窗口。", "专注星球"); Shutdown(); return; }
        var dataKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataIdentity)));
        dataInstance=new Mutex(true,"Local\\FocusGarden-Data-"+dataKey,out bool dataCreated);
        if (!dataCreated) { if(!checkInstallation)MessageBox.Show("这份专注记录正在被另一个窗口使用，请先关闭该窗口。", "专注星球");Shutdown(3);return; }
        DispatcherUnhandledException += (_, args) => { if (MainWindow is MainWindow w) w.ReleaseForError(); MessageBox.Show("发生错误，已解除应用限制。浏览器扩展将在同步时解除。\n" + args.Exception.Message, "专注星球"); args.Handled = true; };
        try
        {
            Directory.CreateDirectory(DataDirectory);
            FocusDataProtection.PrepareForVersion(DataDirectory,CurrentVersion);
            MainWindow = new MainWindow();
            if (checkInstallation)
            {
                var content = (FrameworkElement)MainWindow.Content;
                content.Measure(new Size(1280, 940)); content.Arrange(new Rect(0, 0, 1280, 940)); content.UpdateLayout();
                File.WriteAllText(Path.Combine(DataDirectory, "installation-check.json"), System.Text.Json.JsonSerializer.Serialize(new { Product = "专注星球", Version = CurrentVersion, Ready = true, MainWindow.Title, Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription }));
                MainWindow.Close(); Shutdown(); return;
            }
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            if (checkInstallation) { try { File.WriteAllText(Path.Combine(DataDirectory, "installation-check-error.txt"), ex.ToString()); } catch { } Shutdown(1); return; }
            MessageBox.Show("为保护已有专注记录，本次未能启动。请保留数据目录，检查可用空间或使用兼容版本后重试。\n" + ex.Message, "专注星球"); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { dataInstance?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
