using System.Diagnostics;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed record FocusWorkspaceAppWindow(nint Handle, string ProcessName);
internal sealed record FocusTaskbarWindow(long Handle, bool InitiallyVisible);
internal sealed record FocusForegroundWindow(nint Handle, int ProcessId, string? ProcessName, bool IsDesktop);

internal interface IFocusWorkspaceSession : IDisposable
{
    void Pulse();
    IReadOnlyList<FocusWorkspaceAppWindow> GetAllowedWindows();
    bool ActivateAllowedWindow(nint handle);
}

internal interface IFocusShortcutLease : IDisposable { bool IsHealthy { get; } }
internal interface IFocusRecoveryLease : IDisposable { void Pulse(); bool IsHealthy { get; } }
internal interface IFocusWorkspacePlatform
{
    int ProcessId { get; }
    long Timestamp { get; }
    double ElapsedSeconds(long timestamp);
    IReadOnlyList<FocusTaskbarWindow> GetTaskbars();
    void SetTaskbarVisible(nint handle, bool visible);
    FocusForegroundWindow? GetForeground();
    IReadOnlyList<FocusForegroundWindow> GetWindows();
    bool IsWindow(nint handle);
    bool Minimize(nint handle);
    bool Activate(nint handle);
    Task<IFocusShortcutLease> InstallShortcutsAsync(Func<bool> isActive, CancellationToken cancellationToken);
    Task<IFocusRecoveryLease> StartRecoveryAsync(IReadOnlyList<FocusTaskbarWindow> taskbars, CancellationToken cancellationToken);
}

/// <summary>A per-session lease. Dispose never changes a user's taskbar auto-hide preference.</summary>
internal sealed class FocusWorkspaceGuard : IFocusWorkspaceSession
{
    internal const double HeartbeatTimeoutSeconds = 8;
    private readonly FocusApplicationPolicy policy;
    private readonly IFocusWorkspacePlatform platform;
    private readonly nint owner;
    private readonly IReadOnlyList<FocusTaskbarWindow> taskbars;
    private IFocusShortcutLease? keyboard;
    private IFocusRecoveryLease? recovery;
    private nint lastAllowed;
    private long lastHeartbeat, lastTaskbarCheck;
    private int disposed;
    private bool activated;

    private FocusWorkspaceGuard(FocusApplicationPolicy policy, nint owner, IFocusWorkspacePlatform platform)
    {
        this.policy = policy; this.owner = owner; this.platform = platform;
        taskbars = policy.HideTaskbar ? platform.GetTaskbars() : [];
        lastHeartbeat = lastTaskbarCheck = platform.Timestamp;
    }

    internal static async Task<IFocusWorkspaceSession> StartAsync(FocusApplicationPolicy policy, nint ownerHwnd,
        CancellationToken cancellationToken = default)
    {
        // Do not capture a new "original visibility" while a previous helper is still restoring that shell.
        await FocusShellWatchdog.WaitForPendingRestorationAsync(cancellationToken);
        return await StartAsync(policy, ownerHwnd, new FocusWorkspaceNative(), cancellationToken);
    }

    internal static async Task<IFocusWorkspaceSession> StartAsync(FocusApplicationPolicy policy, nint ownerHwnd,
        IFocusWorkspacePlatform platform, CancellationToken cancellationToken = default)
    {
        if (!policy.WhitelistEnabled) throw new ArgumentException("本轮未启用应用白名单。", nameof(policy));
        if (ownerHwnd == 0 || !platform.IsWindow(ownerHwnd)) throw new InvalidOperationException("专注窗口尚未准备好。");
        var guard = new FocusWorkspaceGuard(policy, ownerHwnd, platform);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = guard.GetAllowedWindows();
            if (windows.Count == 0) throw new InvalidOperationException("请先打开至少一个白名单应用，再开始专注。");
            guard.lastAllowed = windows[0].Handle;
            // The independent helper acknowledges the restore snapshot before any shell window is hidden.
            guard.recovery = await platform.StartRecoveryAsync(guard.taskbars, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref guard.lastHeartbeat, platform.Timestamp);
            if (policy.BlockDesktopShortcuts)
                guard.keyboard = await platform.InstallShortcutsAsync(guard.HasLiveHeartbeat, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!guard.recovery.IsHealthy || guard.keyboard is { IsHealthy: false })
                throw new InvalidOperationException("专注保护未能启动，桌面保持可用。");
            guard.activated = true;
            foreach (var bar in guard.taskbars) platform.SetTaskbarVisible((nint)bar.Handle, false);
            guard.Pulse();
            if (!guard.ActivateAllowedWindow(guard.lastAllowed))
                throw new InvalidOperationException("无法切换到白名单应用，本轮未开启限制。");
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    private bool HasLiveHeartbeat() => Volatile.Read(ref disposed) == 0 &&
        platform.ElapsedSeconds(Interlocked.Read(ref lastHeartbeat)) < HeartbeatTimeoutSeconds;

    public void Pulse()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!HasLiveHeartbeat()) throw new InvalidOperationException("白名单保护超过八秒未收到界面心跳，已恢复桌面，请重新开始。");
        if (recovery is not { IsHealthy: true } || keyboard is { IsHealthy: false })
            throw new InvalidOperationException("白名单保护已停止，已解除本轮限制。");
        recovery.Pulse();
        Interlocked.Exchange(ref lastHeartbeat, platform.Timestamp);
        if (policy.HideTaskbar)
        {
            if (platform.ElapsedSeconds(lastTaskbarCheck) >= 2)
            {
                lastTaskbarCheck = platform.Timestamp;
                var current = platform.GetTaskbars().Select(b => b.Handle).Order().ToArray();
                if (!current.SequenceEqual(taskbars.Select(b => b.Handle).Order()))
                    throw new InvalidOperationException("桌面或显示器配置发生变化，本轮白名单保护已停止。");
            }
            foreach (var bar in taskbars) platform.SetTaskbarVisible((nint)bar.Handle, false);
        }
        var foreground = platform.GetForeground();
        // A null foreground also occurs on Windows' secure desktop. Never steal that desktop's focus.
        if (foreground is null || foreground.Handle == 0) return;
        if (Allowed(foreground))
        {
            if (foreground.ProcessId != platform.ProcessId && !FocusApplicationPolicy.IsSystemSafetyApp(foreground.ProcessName!))
                lastAllowed = foreground.Handle;
            return;
        }
        if (!foreground.IsDesktop && !platform.Minimize(foreground.Handle))
            throw new InvalidOperationException("无法拦回名单外窗口，本轮白名单保护已停止。");
        var allowed = GetAllowedWindows();
        var target = allowed.Any(w => w.Handle == lastAllowed) ? lastAllowed : allowed.FirstOrDefault()?.Handle ?? owner;
        if (!platform.Activate(target))
            throw new InvalidOperationException("无法返回白名单工作区，本轮白名单保护已停止。");
    }

    private bool Allowed(FocusForegroundWindow window)
    {
        if (window.ProcessId == platform.ProcessId) return true;
        if (string.IsNullOrWhiteSpace(window.ProcessName)) return false;
        try { return policy.IsAllowed(window.ProcessName); }
        catch (ArgumentException) { return false; }
    }

    public IReadOnlyList<FocusWorkspaceAppWindow> GetAllowedWindows()
    {
        if (Volatile.Read(ref disposed) != 0) return [];
        return platform.GetWindows().Where(w => w.ProcessId != platform.ProcessId && !w.IsDesktop &&
            !string.IsNullOrWhiteSpace(w.ProcessName) && !FocusApplicationPolicy.IsSystemSafetyApp(w.ProcessName) && Allowed(w))
            .Select(w => new FocusWorkspaceAppWindow(w.Handle, w.ProcessName!)).ToArray();
    }

    public bool ActivateAllowedWindow(nint handle)
    {
        if (Volatile.Read(ref disposed) != 0 || !GetAllowedWindows().Any(w => w.Handle == handle)) return false;
        if (!platform.Activate(handle)) return false;
        lastAllowed = handle; return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Each restore is independent: one missing shell window cannot prevent the other displays or hook from recovering.
        try { keyboard?.Dispose(); } catch { }
        if (activated)
            foreach (var bar in taskbars)
                if (bar.InitiallyVisible) try { platform.SetTaskbarVisible((nint)bar.Handle, true); } catch { }
        // Closing the pipe also makes the helper repeat restoration if this process failed part-way through it.
        try { recovery?.Dispose(); } catch { }
    }
}
