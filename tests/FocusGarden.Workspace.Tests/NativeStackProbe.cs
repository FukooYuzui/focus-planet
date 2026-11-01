using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using FocusGarden.App;

/// <summary>Opt-in Win32 order check. Only private offscreen tool windows are ever positioned.</summary>
internal static class NativeStackProbe
{
    private const uint ToolNoActivate = 0x00000080 | 0x08000000;
    private const uint NoSizeMoveActivateOwner = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private static readonly List<nint> owned = [];

    internal static int Run()
    {
        try
        {
            var foreground = GetForegroundWindow();
            var left = GetSystemMetrics(76) - 2048;
            var top = GetSystemMetrics(77) - 2048;
            var editor = Create("allowed", left, top);
            var minimized = Create("allowed-minimized", left - 80, top);
            var backdrop = Create("backdrop", left - 160, top);
            var other = Create("unlisted", left - 240, top);
            var native = new FocusWorkspaceNative();
            var exStyles = owned.ToDictionary(h => h, h => GetWindowLong(h, -20));
            Position(backdrop, 0); Position(editor, 0); Position(other, 0);
            var snapshot = Snapshot(native);
            ValidatePlan(snapshot, [backdrop], [editor, minimized]);
            native.MaintainBackdropOrder([backdrop], [editor, minimized], snapshot);
            var ordered = Snapshot(native);
            Assert(Before(ordered, editor, backdrop) && Before(ordered, minimized, backdrop) && Before(ordered, backdrop, other),
                "Native SetWindowPos did not place permitted windows above backdrop and unrelated window below it.");
            Assert(FocusWorkspaceStack.Plan(ordered, [backdrop], [editor, minimized]).Count == 0,
                "Stable native order still requests movement.");
            native.MaintainBackdropOrder([backdrop], [editor, minimized], ordered);
            Console.WriteLine("PASS native offscreen order and steady-state idempotence");

            MinimizeOffscreen(minimized, left - 80, top);
            MinimizeOffscreen(backdrop, left - 160, top);
            Assert(IsIconic(minimized) && IsIconic(backdrop), "Synthetic minimized windows were not minimized.");
            snapshot = Snapshot(native);
            ValidatePlan(snapshot.Select(w => w.Handle == backdrop ? w with { IsMinimized = false } : w).ToArray(), [backdrop], [editor, minimized]);
            native.MaintainBackdropOrder([backdrop], [editor, minimized], snapshot);
            Assert(!IsIconic(backdrop) && IsIconic(minimized), "Backdrop restore also restored a deliberately minimized allowed app.");
            Assert(GetWindowRect(backdrop, out var bounds) && bounds.Right < GetSystemMetrics(76) && bounds.Bottom < GetSystemMetrics(77),
                "Restored private backdrop is not strictly offscreen.");
            ordered = Snapshot(native);
            Assert(Before(ordered, editor, backdrop) && Before(ordered, backdrop, other), "Restoration did not repair native order.");
            Console.WriteLine("PASS native offscreen backdrop restore preserves minimized allowed app");

            Assert(owned.All(h => GetWindowLong(h, -20) == exStyles[h]), "A synthetic window's extended style was changed.");
            Assert(owned.All(h => (GetWindowLong(h, -20) & 0x8) == 0), "A synthetic window was promoted to topmost.");
            Assert(GetForegroundWindow() == foreground, "Foreground changed during this no-activation probe.");
            Console.WriteLine("PASS native style stability and no foreground activation");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                NativeStackCasesPassed = 3, OffscreenWindows = owned.Count, RealTaskbarHandles = 0,
                KeyboardHookInstalled = false, ForegroundActivationRequested = false,
                Scope = "Only private offscreen nonactivating tool windows were positioned; no real app was minimized or activated."
            }));
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL native stack probe: " + error); return 1; }
        finally
        {
            foreach (var handle in owned)
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid == Environment.ProcessId) DestroyWindow(handle);
            }
            owned.Clear();
        }
    }

    private static nint Create(string name, int left, int top)
    {
        var handle = CreateWindowEx(ToolNoActivate, "STATIC", "FocusPlanet-QA-" + name,
            0x80000000, left, top, 24, 24, 0, 0, GetModuleHandle(null), 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        owned.Add(handle);
        if (!SetWindowPos(handle, (nint)1, left, top, 24, 24, 0x0010 | 0x0040 | 0x0200))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    private static IReadOnlyList<FocusForegroundWindow> Snapshot(FocusWorkspaceNative native) =>
        native.GetWindows().Where(w => owned.Contains(w.Handle)).ToArray();
    private static bool Before(IReadOnlyList<FocusForegroundWindow> windows, nint first, nint second) =>
        windows.Select(w => w.Handle).ToList().IndexOf(first) is var i && i >= 0 &&
        windows.Select(w => w.Handle).ToList().IndexOf(second) > i;
    private static void ValidatePlan(IReadOnlyList<FocusForegroundWindow> windows, IReadOnlyList<nint> backdrops, IReadOnlyList<nint> permitted)
    {
        Assert(backdrops.Concat(permitted).All(owned.Contains), "Probe attempted to provide an external placement target.");
        Assert(FocusWorkspaceStack.Plan(windows, backdrops, permitted)
            .All(p => owned.Contains(p.Handle) && (p.InsertAfter == 0 || owned.Contains(p.InsertAfter))),
            "Probe produced an external placement target.");
    }
    private static void Position(nint handle, nint after)
    {
        Assert(owned.Contains(handle) && (after == 0 || owned.Contains(after)), "Non-owned window placement rejected.");
        if (!SetWindowPos(handle, after, 0, 0, 0, 0, NoSizeMoveActivateOwner))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void MinimizeOffscreen(nint handle, int left, int top)
    {
        Assert(owned.Contains(handle), "Non-owned minimize rejected.");
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref placement)) throw new Win32Exception(Marshal.GetLastWin32Error());
        placement.Flags = 1; placement.ShowCommand = 7; // SETMINPOSITION, SHOWMINNOACTIVE
        placement.MinPosition = new Point { X = left, Y = top };
        placement.NormalPosition = new Rect { Left = left, Top = top, Right = left + 24, Bottom = top + 24 };
        if (!SetWindowPlacement(handle, ref placement)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowPos(handle, 0, left, top, 24, 24, 0x0010 | 0x0004 | 0x0200))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
