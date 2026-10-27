using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FocusGarden.Core;

namespace FocusGarden.App;

/// <summary>Read-only Win32 foreground and idle sampling. No titles, URLs or input contents are collected.</summary>
internal sealed class AppUsageTracker
{
    private readonly AppUsageStore store;
    private readonly UsageAccumulator accumulator = new();
    private readonly List<AppUsageCredit> pending = new();
    private bool enabled = true, isSessionLocked;
    private long lastFlush = Stopwatch.GetTimestamp();
    private uint cachedPid;
    private string? cachedName;
    private long cachedAt;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "system", "registry", "secure system", "lockapp", "logonui", "winlogon", "dwm",
        "sihost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "textinputhost", "consent"
    };
    public string? LastError { get; private set; }
    public string StateText { get; private set; } = "等待首次前台采样";

    public AppUsageTracker(AppUsageStore store) => this.store = store ?? throw new ArgumentNullException(nameof(store));

    public bool Enabled
    {
        get => enabled;
        set { if (enabled == value) return; Flush(); enabled = value; accumulator.ResetBaseline(); StateText = value ? "等待下一次前台采样" : "已暂停统计"; }
    }
    public bool IsSessionLocked
    {
        get => isSessionLocked;
        set { if (isSessionLocked == value) return; Flush(); isSessionLocked = value; accumulator.ResetBaseline(); StateText = value ? "系统已锁定，暂停统计" : "等待下一次前台采样"; }
    }

    public void Sample()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.Now;
        string? name = null;
        var idle = 60.0;
        if (enabled && !isSessionLocked)
        {
            var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (GetLastInputInfo(ref info))
            {
                idle = unchecked((uint)Environment.TickCount64 - info.dwTime) / 1000.0;
                var window = GetForegroundWindow();
                if (window != 0 && !IsDesktopSurface(window))
                {
                    GetWindowThreadProcessId(window, out var pid);
                    if (pid != 0)
                    {
                        try
                        {
                            if (pid != cachedPid || Stopwatch.GetElapsedTime(cachedAt, timestamp).TotalSeconds >= 5)
                            {
                                using var process = Process.GetProcessById((int)pid);
                                cachedName = process.ProcessName;
                                cachedPid = pid; cachedAt = timestamp;
                            }
                            if (cachedName != null && !Excluded.Contains(cachedName)) name = cachedName + ".exe";
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        { cachedPid = 0; cachedName = null; }
                    }
                }
            }
        }
        pending.AddRange(accumulator.Sample(new AppUsageSample(name, (double)timestamp / Stopwatch.Frequency, now, idle, enabled, isSessionLocked)));
        StateText = accumulator.StateText;
        if (Stopwatch.GetElapsedTime(lastFlush, timestamp).TotalSeconds >= 5) Flush();
    }

    /// <summary>Attempts an atomic flush; on failure the batch remains pending and may be retried without duplication.</summary>
    public void Flush()
    {
        lastFlush = Stopwatch.GetTimestamp();
        if (pending.Count == 0) return;
        try { store.AddCredits(pending); pending.Clear(); LastError = null; }
        catch (Exception ex) { LastError = "应用时长暂未保存：" + ex.Message; }
    }

    /// <summary>Includes unflushed integer seconds. Call on the same UI thread as Sample and Flush.</summary>
    public IReadOnlyList<AppUsageRow> GetDailyUsage(DateTime date)
    {
        var totals = store.GetDailyUsage(date).ToDictionary(r => r.AppName, r => r.Seconds, StringComparer.OrdinalIgnoreCase);
        foreach (var credit in pending.Where(c => c.Day == date.Date))
        {
            totals.TryGetValue(credit.AppName, out var seconds);
            totals[credit.AppName] = checked(seconds + credit.Seconds);
        }
        return totals.Select(t => new AppUsageRow(t.Key, t.Value)).OrderByDescending(r => r.Seconds).ThenBy(r => r.AppName, StringComparer.Ordinal).ToArray();
    }

    private static bool IsDesktopSurface(nint window)
    {
        if (window == GetShellWindow() || window == GetDesktopWindow()) return true;
        // Window classes identify shell surfaces without collecting the user-visible title.
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
