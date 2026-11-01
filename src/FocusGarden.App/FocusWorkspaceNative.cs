using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusGarden.App;

internal sealed class FocusWorkspaceNative : IFocusWorkspacePlatform
{
    private readonly Dictionary<int, (string? Name, long Checked)> names = [];
    public int ProcessId => Environment.ProcessId;
    public long Timestamp => Stopwatch.GetTimestamp();
    public double ElapsedSeconds(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
    public IReadOnlyList<FocusTaskbarWindow> GetTaskbars() => FocusShellNative.Taskbars();
    public void SetTaskbarVisible(nint handle, bool visible) => FocusShellNative.SetVisible(handle, visible);
    public bool IsWindow(nint handle) => FocusShellNative.IsWindow(handle);
    public FocusForegroundWindow? GetForeground()
    {
        var handle = GetForegroundWindow();
        return handle == 0 ? null : Describe(handle);
    }
    public IReadOnlyList<FocusForegroundWindow> GetWindows()
    {
        var result = new List<FocusForegroundWindow>();
        FocusShellNative.EnumWindows((window, _) =>
        {
            // Owned modal dialogs can be the only usable window of an application.
            if (FocusShellNative.IsWindowVisible(window) &&
                (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0))
                result.Add(Describe(window));
            return true;
        }, 0);
        return result;
    }
    private FocusForegroundWindow Describe(nint handle)
    {
        GetWindowThreadProcessId(handle, out var unsignedPid);
        var pid = unchecked((int)unsignedPid);
        var className = FocusShellNative.ClassName(handle);
        var desktop = className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        if (!names.TryGetValue(pid, out var known) || Stopwatch.GetElapsedTime(known.Checked).TotalSeconds >= 2)
        {
            string? name = null;
            try { using var process = Process.GetProcessById(pid); name = process.ProcessName; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
            if (names.Count > 256) names.Clear();
            names[pid] = known = (name, Stopwatch.GetTimestamp());
        }
        var style = IntPtr.Size == 8 ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20);
        return new(handle, pid, known.Name, desktop, IsIconic(handle), (style & 0x8) != 0);
    }
    public bool Minimize(nint handle)
    {
        // The foreground snapshot may already have closed or minimized while we inspected it.
        if (!FocusShellNative.IsWindow(handle) || IsIconic(handle)) return true;
        return ShowWindowAsync(handle, 6) || !FocusShellNative.IsWindow(handle) || IsIconic(handle);
    }
    public bool Activate(nint handle)
    {
        if (!FocusShellNative.IsWindow(handle)) return false;
        if (IsIconic(handle) && !ShowWindowAsync(handle, 9) && IsIconic(handle)) return false;
        if (GetForegroundWindow() == handle || SetForegroundWindow(handle)) return true;
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread || !AttachThreadInput(currentThread, foregroundThread, true)) return false;
        try { return SetForegroundWindow(handle); }
        finally { AttachThreadInput(currentThread, foregroundThread, false); }
    }
    public void MaintainBackdropOrder(IReadOnlyList<nint> backdrops, IReadOnlyList<nint> permitted, IReadOnlyList<FocusForegroundWindow> windows)
    {
        const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE, NOMOVE, NOACTIVATE, NOOWNERZORDER
        var snapshot = windows.ToList();
        var restoredAny = false;
        foreach (var handle in backdrops)
        {
            var index = snapshot.FindIndex(w => w.Handle == handle);
            var state = index >= 0 ? snapshot[index] : Describe(handle);
            if (!FocusShellNative.IsWindow(handle) || state.ProcessId != ProcessId)
                throw new InvalidOperationException("专注背景窗口已失效。");
            if (index < 0 && DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                throw new InvalidOperationException("Windows 桌面已切换，本轮白名单保护已停止。");
            // These are our own UI-thread windows. Restore them synchronously without activating
            // an app which the user deliberately minimized, including after Show Desktop.
            if (state.IsMinimized || index < 0)
            {
                ShowWindow(handle, 4); restoredAny = true;
                if (IsIconic(handle) || !FocusShellNative.IsWindowVisible(handle))
                    throw new InvalidOperationException("无法恢复专注背景。");
            }
            if (state.IsTopmost && !SetWindowPos(handle, (nint)(-2), 0, 0, 0, 0, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复专注背景层级。");
            restoredAny |= state.IsTopmost;
            var restored = state with { IsMinimized = false, IsTopmost = false };
            if (index >= 0) snapshot[index] = restored;
            else snapshot.Add(restored);
        }
        // Restoring our own window may have changed its z-order. Re-read only on that rare path;
        // normal heartbeats use the single snapshot already captured by the guard.
        if (restoredAny) snapshot = GetWindows().ToList();
        foreach (var placement in FocusWorkspaceStack.Plan(snapshot, backdrops, permitted))
        {
            // A permitted app can close between enumeration and placement. It no longer needs
            // to be above the backdrop; the next heartbeat recomputes the remaining order.
            if (!FocusShellNative.IsWindow(placement.Handle)) continue;
            if (placement.InsertAfter != 0 && !FocusShellNative.IsWindow(placement.InsertAfter)) continue;
            if (!SetWindowPos(placement.Handle, placement.InsertAfter, 0, 0, 0, 0, flags) &&
                backdrops.Contains(placement.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法显示专注背景。");
        }
    }
    public Task<IFocusShortcutLease> InstallShortcutsAsync(Func<bool> isActive, CancellationToken cancellationToken) =>
        FocusKeyboardLease.StartAsync(isActive, cancellationToken);
    public Task<IFocusRecoveryLease> StartRecoveryAsync(IReadOnlyList<FocusTaskbarWindow> taskbars, CancellationToken cancellationToken) =>
        FocusShellWatchdog.StartAsync(taskbars, cancellationToken);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}

internal sealed record FocusWindowPlacement(nint Handle, nint InsertAfter);

/// <summary>Compute the smallest prefix repair of normal windows; topmost styles and minimized apps are untouched.</summary>
internal static class FocusWorkspaceStack
{
    internal static IReadOnlyList<FocusWindowPlacement> Plan(IReadOnlyList<FocusForegroundWindow> windows,
        IReadOnlyList<nint> backdrops, IReadOnlyList<nint> permitted)
    {
        var current = windows.Where(w => !w.IsMinimized && !w.IsTopmost && !w.IsDesktop)
            .Select(w => w.Handle).Distinct().ToList();
        var desired = current.Where(h => permitted.Contains(h) && !backdrops.Contains(h))
            .Concat(backdrops.Where(current.Contains)).Distinct().ToArray();
        var result = new List<FocusWindowPlacement>();
        for (var i = 0; i < desired.Length; i++)
        {
            if (current[i] == desired[i]) continue;
            current.Remove(desired[i]); current.Insert(i, desired[i]);
            result.Add(new(desired[i], i == 0 ? 0 : desired[i - 1]));
        }
        return result;
    }
}

internal static class FocusShellNative
{
    internal delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder buffer, int maximum);
    internal static string ClassName(nint window)
    {
        var text = new StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString();
    }
    internal static IReadOnlyList<FocusTaskbarWindow> Taskbars()
    {
        var result = new List<FocusTaskbarWindow>();
        EnumWindows((window, _) =>
        {
            if (ClassName(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                result.Add(new(window.ToInt64(), IsWindowVisible(window)));
            return true;
        }, 0);
        return result;
    }
    internal static void SetVisible(nint handle, bool visible)
    {
        if (!IsWindow(handle) || ClassName(handle) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
        {
            if (visible) return; // Explorer may have restarted; never operate on a reused non-taskbar HWND.
            throw new InvalidOperationException("任务栏已变化，已停止白名单保护。");
        }
        if (IsWindowVisible(handle) == visible) return;
        if (!ShowWindowAsync(handle, visible ? 5 : 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法更新任务栏显示状态。");
    }
    internal static void Restore(IEnumerable<FocusTaskbarWindow> snapshot)
    {
        foreach (var bar in snapshot)
            if (bar.InitiallyVisible) try { SetVisible((nint)bar.Handle, true); } catch { }
    }
    internal static async Task WaitForRestorationAsync(IReadOnlyList<FocusTaskbarWindow> snapshot)
    {
        var deadline = Stopwatch.StartNew();
        bool Pending() => snapshot.Any(bar => bar.InitiallyVisible && IsWindow((nint)bar.Handle) &&
            ClassName((nint)bar.Handle) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && !IsWindowVisible((nint)bar.Handle));
        while (Pending() && deadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(25).ConfigureAwait(false);
        if (Pending()) throw new InvalidOperationException("上轮任务栏尚未恢复，暂不能开始新的白名单专注。");
    }
}

internal sealed class FocusKeyboardLease : IFocusShortcutLease
{
    private delegate nint HookProc(int code, nint message, nint data);
    private readonly Func<bool> active;
    private readonly FocusShortcutPolicy policy = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly HookProc callback;
    private nint hook;
    private uint threadId;
    private int disposed;
    private Exception? failure;
    public bool IsHealthy => Volatile.Read(ref disposed) == 0 && Volatile.Read(ref failure) is null && thread.IsAlive && hook != 0;

    private FocusKeyboardLease(Func<bool> active)
    {
        this.active = active; callback = Handle;
        thread = new Thread(Run) { IsBackground = true, Name = "FocusPlanet-shortcut-filter" };
        thread.SetApartmentState(ApartmentState.STA);
    }
    internal static async Task<IFocusShortcutLease> StartAsync(Func<bool> active, CancellationToken token)
    {
        var lease = new FocusKeyboardLease(active); lease.thread.Start();
        try { await lease.ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token); return lease; }
        catch { lease.Dispose(); throw; }
    }
    private void Run()
    {
        try
        {
            threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, 0); // Establish the message queue before readiness and cancellation.
            if (Volatile.Read(ref disposed) != 0) { ready.TrySetCanceled(); return; }
            if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0)
                throw new InvalidOperationException("请先松开 Windows 键，再开始白名单专注。");
            foreach (var key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
                if (GetAsyncKeyState(key) < 0) policy.SeedModifier(key);
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "键盘快捷键保护无法启动。");
            ready.TrySetResult(true);
            while (Volatile.Read(ref disposed) == 0)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
        }
        catch (Exception error) { Volatile.Write(ref failure, error); ready.TrySetException(error); }
        finally { var saved = hook; hook = 0; if (saved != 0) UnhookWindowsHookEx(saved); }
    }
    private nint Handle(int code, nint message, nint data)
    {
        try
        {
            if (code < 0 || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref failure) != null || !active())
                return CallNextHookEx(hook, code, message, data);
            bool down = message is 0x0100 or 0x0104;
            bool up = message is 0x0101 or 0x0105;
            if (!down && !up) return CallNextHookEx(hook, code, message, data);
            var key = Marshal.ReadInt32(data);
            var decision = policy.Decide(key, down);
            if (decision == FocusKeyDecision.LockWorkstation && !LockWorkStation())
                Volatile.Write(ref failure, new Win32Exception(Marshal.GetLastWin32Error(), "无法进入系统锁屏。"));
            if (decision != FocusKeyDecision.Pass) return 1;
        }
        catch (Exception error) { Volatile.Write(ref failure, error); }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (threadId != 0) PostThreadMessage(threadId, 0x0012, 0, 0);
        // No join on the WPF thread. The callback already passes all input and the hook thread cleans itself up.
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
}
