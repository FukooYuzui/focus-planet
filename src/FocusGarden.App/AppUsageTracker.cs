using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FocusGarden.Core;

namespace FocusGarden.App;

/// <summary>Read-only Win32 foreground and idle sampling. No titles, URLs or input contents are collected.</summary>
internal sealed class AppUsageTracker
{
    private readonly AppUsageStore store;
    private readonly SerialWorkQueue work;
    private readonly object stateGate = new();
    private readonly UsageAccumulator accumulator = new();
    private readonly List<AppUsageCredit> pending = new();
    private Task<bool> flushTask = Task.FromResult(true);
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

    public AppUsageTracker(AppUsageStore store, SerialWorkQueue work)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.work = work ?? throw new ArgumentNullException(nameof(work));
    }

    public bool Enabled
    {
        get => enabled;
        set
        {
            lock (stateGate) { if (enabled == value) return; enabled = value; accumulator.ResetBaseline(); StateText = value ? "等待下一次前台采样" : "已暂停统计"; }
            _ = FlushAsync();
        }
    }
    public bool IsSessionLocked
    {
        get => isSessionLocked;
        set
        {
            lock (stateGate) { if (isSessionLocked == value) return; isSessionLocked = value; accumulator.ResetBaseline(); StateText = value ? "系统已锁定，暂停统计" : "等待下一次前台采样"; }
            _ = FlushAsync();
        }
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
        Observe(new AppUsageSample(name, (double)timestamp / Stopwatch.Frequency, now, idle, enabled, isSessionLocked));
        if (Stopwatch.GetElapsedTime(lastFlush, timestamp).TotalSeconds >= 5) _ = FlushAsync();
    }

    /// <summary>The same conservative sample path is used by Win32 sampling and synthetic regression fixtures.</summary>
    internal void Observe(AppUsageSample sample)
    {
        lock (stateGate)
        {
            pending.AddRange(accumulator.Sample(sample));
            StateText = accumulator.StateText;
        }
    }

    /// <summary>Only immutable batches reach the background owner. Failure restores that exact batch once.</summary>
    public Task<bool> FlushAsync()
    {
        lock (stateGate)
        {
            lastFlush = Stopwatch.GetTimestamp();
            if (!flushTask.IsCompleted) return flushTask;
            if (pending.Count == 0) return Task.FromResult(true);
            var batch = pending.ToArray();
            pending.Clear();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            flushTask = completion.Task;
            _ = PersistBatchAsync(batch, completion);
            return flushTask;
        }
    }

    private async Task PersistBatchAsync(AppUsageCredit[] batch, TaskCompletionSource<bool> completion)
    {
        try
        {
            await work.RunAsync(() => store.AddCredits(batch)).ConfigureAwait(false);
            lock (stateGate) LastError = null;
            completion.SetResult(true);
        }
        catch (Exception ex)
        {
            lock (stateGate) { pending.InsertRange(0, batch); LastError = "应用时长暂未保存：" + ex.Message; }
            completion.SetResult(false);
        }
    }

    /// <summary>Used on orderly close. A failed batch remains available for an explicit retry.</summary>
    public async Task<bool> FlushAllAsync()
    {
        while (true)
        {
            if (!await FlushAsync().ConfigureAwait(false)) return false;
            lock (stateGate) if (pending.Count == 0 && flushTask.IsCompleted) return true;
        }
    }

    /// <summary>Reads a queue-ordered snapshot plus the then-unsubmitted credits, without waiting on the UI thread.</summary>
    public async Task<IReadOnlyList<AppUsageRow>> GetDailyUsageAsync(DateTime date)
    {
        AppUsageCredit[] snapshot;
        Task<IReadOnlyList<AppUsageRow>> query;
        while (true)
        {
            Task<bool> outstanding;
            lock (stateGate)
            {
                outstanding = flushTask;
                if (outstanding.IsCompleted)
                {
                    snapshot = pending.ToArray();
                    // Enqueue under the sampling-state lock, before a later flush can enqueue this snapshot.
                    query = work.RunAsync(() => store.GetDailyUsage(date));
                    break;
                }
            }
            await outstanding.ConfigureAwait(false);
        }
        var rows = await query.ConfigureAwait(false);
        var totals = rows.ToDictionary(r => r.AppName, r => r.Seconds, StringComparer.OrdinalIgnoreCase);
        foreach (var credit in snapshot.Where(c => c.Day == date.Date))
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
