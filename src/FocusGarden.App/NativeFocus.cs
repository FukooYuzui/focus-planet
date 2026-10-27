using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FocusGarden.App;
internal static class NativeFocus
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    internal static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "focusgarden", "专注星球", "focusplanet", "taskmgr", "explorer", "winlogon", "logonui", "csrss", "lsass", "services", "system", "registry", "dwm", "sihost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "applicationframehost", "secure system", "smss", "svchost", "runtimebroker", "conhost", "codex", "chatgpt" };
    internal static string? BlockForeground(string[] names)
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId || pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            if (Protected.Contains(name) || FocusGarden.Core.AppRules.IsProtected(name) || !names.Any(n => string.Equals(n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n, name, StringComparison.OrdinalIgnoreCase))) return null;
            return ShowWindowAsync(hwnd, 6) ? name : null;
        }
        catch { return null; }
    }
}
