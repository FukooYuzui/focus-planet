using System.Diagnostics;
using FocusGarden.App;
using FocusGarden.Core;

if (args is ["--verify-published-helper", var targetExecutable])
    return await PublishedHelperProbe.RunAsync(targetExecutable);
if (await FocusShellWatchdog.TryRunAsync(args) is { } helperExit) return helperExit;
int passed = 0, failed = 0;
void Assert(bool condition, string? message = null) { if (!condition) throw new Exception(message ?? "Assertion failed"); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
Task Check(Action action) { action(); return Task.CompletedTask; }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
FocusApplicationPolicy Policy(bool hide = true, bool keys = true) => FocusApplicationPolicy.Create(new FocusSettings
{
    WhitelistEnabled = true, AllowedApps = ["word.exe", "notepad.exe"], HideTaskbar = hide, BlockDesktopShortcuts = keys
});

await Test("watchdog acknowledgement always precedes hiding taskbars", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    Assert(platform.Events.IndexOf("recovery-ready") < platform.Events.IndexOf("bar:1:False"));
    Assert(platform.Events.IndexOf("keyboard-ready") < platform.Events.IndexOf("bar:1:False"));
    Assert(platform.Events.Contains("activate:200"));
});
await Test("no open whitelist window fails before hook helper or taskbar changes", async () =>
{
    var platform = new FakePlatform(); platform.Windows.RemoveAll(w => w.ProcessId != platform.ProcessId);
    await Throws<InvalidOperationException>(async () => await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform));
    Assert(platform.Events.Count == 0);
});
await Test("unready recovery helper never hides a taskbar", async () =>
{
    var platform = new FakePlatform { FailRecovery = true };
    await Throws<IOException>(async () => await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform));
    Assert(!platform.Events.Any(e => e.StartsWith("bar:")));
});
await Test("keyboard startup failure closes recovery without hiding taskbars", async () =>
{
    var platform = new FakePlatform { FailKeyboard = true };
    await Throws<IOException>(async () => await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform));
    Assert(platform.Recovery.Disposed && !platform.Events.Any(e => e.StartsWith("bar:")));
});
await Test("cancelled asynchronous startup releases ready helper before changing desktop", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var platform = new FakePlatform { RecoveryReady = cancellation.Cancel };
    await Throws<OperationCanceledException>(async () => await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform, cancellation.Token));
    Assert(platform.Recovery.Disposed && !platform.Events.Any(e => e.StartsWith("bar:")));
});
await Test("partial taskbar hide failure restores visible taskbars and removes the hook", async () =>
{
    var platform = new FakePlatform { HideFailureHandle = 2 };
    await Throws<IOException>(async () => await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform));
    Assert(platform.Events.Contains("bar:1:True") && !platform.Events.Contains("bar:2:True"));
    Assert(platform.Keyboard.Disposed && platform.Recovery.Disposed);
});
await Test("dispose is idempotent and restores only the original visible shell windows", async () =>
{
    var platform = new FakePlatform(); var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    guard.Dispose(); guard.Dispose();
    Assert(platform.Events.Count(e => e == "bar:1:True") == 1 && !platform.Events.Contains("bar:2:True"));
    Assert(platform.Keyboard.Disposed && platform.Recovery.Disposed);
});
await Test("one failing restoration does not skip other displays or recovery helper", async () =>
{
    var platform = new FakePlatform { RestoreFailureHandle = 1 }; platform.Bars = [new(1, true), new(2, true)];
    var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform); guard.Dispose();
    Assert(platform.Events.Contains("bar:2:True") && platform.Recovery.Disposed && platform.Keyboard.Disposed);
});
await Test("permitted foreground apps and recovery tools are not repeatedly reactivated", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.Events.Clear();
    foreach (var foreground in new[] { new FocusForegroundWindow(200, 2, "word", false), new(100, 1, "focusgarden", false), new(800, 8, "taskmgr", false) })
    {
        platform.Foreground = foreground; guard.Pulse();
    }
    Assert(!platform.Events.Any(e => e.StartsWith("activate:") || e.StartsWith("minimize:")));
});
await Test("unlisted app is minimized and last allowed app regains focus without process termination", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.Foreground = new(300, 3, "notepad", false); guard.Pulse();
    platform.Events.Clear(); platform.Foreground = new(400, 4, "chrome", false); guard.Pulse();
    Assert(platform.Events.Contains("minimize:400") && platform.Events.Contains("activate:300"));
});
await Test("desktop returns to owned focus window when allowed apps have closed", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.Windows.RemoveAll(w => w.ProcessId != platform.ProcessId);
    platform.Foreground = new(500, 5, "explorer", true); platform.Events.Clear(); guard.Pulse();
    Assert(platform.Events.Contains("activate:100") && !platform.Events.Any(e => e.StartsWith("minimize:")));
});
await Test("picker contains only explicit allowed windows and cannot activate arbitrary handles", async () =>
{
    var platform = new FakePlatform(); platform.Windows.Add(new(800, 8, "taskmgr", false)); platform.Windows.Add(new(400, 4, "chrome", false));
    using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    Assert(guard.GetAllowedWindows().Select(w => w.Handle).SequenceEqual(new nint[] { 200, 300 }));
    Assert(!guard.ActivateAllowedWindow(400) && !guard.ActivateAllowedWindow(800) && guard.ActivateAllowedWindow(300));
});
await Test("unknown process is restricted while a secure desktop with no foreground is left alone", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.Events.Clear(); platform.Foreground = null; guard.Pulse();
    Assert(!platform.Events.Any(e => e.StartsWith("activate:")));
    platform.Foreground = new(900, 9, null, false); guard.Pulse(); Assert(platform.Events.Contains("minimize:900"));
});
await Test("failed foreground control is surfaced so caller can interrupt the session", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.FailMinimize = true; platform.Foreground = new(400, 4, "chrome", false);
    await Throws<InvalidOperationException>(() => Check(guard.Pulse));
});
await Test("lost helper or keyboard lease is reported instead of silently losing enforcement", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.Recovery.Healthy = false; await Throws<InvalidOperationException>(() => Check(guard.Pulse));
    platform.Recovery.Healthy = true; platform.Keyboard.Healthy = false;
    await Throws<InvalidOperationException>(() => Check(guard.Pulse));
});
await Test("eight-second UI heartbeat expiration releases shortcut predicate and rejects resumed pulse", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    Assert(platform.Active!()); platform.Now += 8001;
    Assert(!platform.Active!()); await Throws<InvalidOperationException>(() => Check(guard.Pulse));
});
await Test("taskbar recreation or monitor changes stop protection instead of hiding untracked windows", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(), 100, platform);
    platform.Now += 2100; platform.Bars = [new(3, true)];
    await Throws<InvalidOperationException>(() => Check(guard.Pulse));
    Assert(!platform.Events.Contains("bar:3:False"));
});
await Test("optional taskbar and keyboard settings do not modify those surfaces", async () =>
{
    var platform = new FakePlatform(); using var guard = await FocusWorkspaceGuard.StartAsync(Policy(false, false), 100, platform);
    Assert(!platform.Events.Any(e => e.StartsWith("bar:") || e == "keyboard-ready"));
});
await Test("ordinary typing editing shortcuts and function keys pass unchanged", () => Check(() =>
{
    var keys = new FocusShortcutPolicy();
    foreach (int key in new[] { 0x41, 0x20, 0x0D, 0x08, 0x7A }) { Assert(keys.Decide(key, true) == FocusKeyDecision.Pass); Assert(keys.Decide(key, false) == FocusKeyDecision.Pass); }
    Assert(keys.Decide(0xA2, true) == FocusKeyDecision.Pass);
    foreach (int key in new[] { 0x43, 0x56, 0x53, 0x5A }) { Assert(keys.Decide(key, true) == FocusKeyDecision.Pass); Assert(keys.Decide(key, false) == FocusKeyDecision.Pass); }
    Assert(keys.Decide(0xA2, false) == FocusKeyDecision.Pass);
}));
await Test("both Windows keys and desktop switching chords suppress paired key transitions", () => Check(() =>
{
    foreach (int win in new[] { 0x5B, 0x5C }) foreach (int action in new[] { 0x44, 0x09, 0x4D, 0x45, 0x54, 0x31 })
    {
        var keys = new FocusShortcutPolicy();
        Assert(keys.Decide(win, true) == FocusKeyDecision.Suppress);
        Assert(keys.Decide(action, true) == FocusKeyDecision.Suppress);
        Assert(keys.Decide(action, false) == FocusKeyDecision.Suppress);
        Assert(keys.Decide(win, false) == FocusKeyDecision.Suppress);
        Assert(keys.Decide(0x41, true) == FocusKeyDecision.Pass);
    }
}));
await Test("Alt Tab Escape F4 Space and Ctrl Escape are blocked without suppressing modifier release", () => Check(() =>
{
    foreach (int alt in new[] { 0xA4, 0xA5 }) foreach (int action in new[] { 0x09, 0x1B, 0x73, 0x20 })
    {
        var keys = new FocusShortcutPolicy(); Assert(keys.Decide(alt, true) == FocusKeyDecision.Pass);
        Assert(keys.Decide(action, true) == FocusKeyDecision.Suppress); Assert(keys.Decide(action, false) == FocusKeyDecision.Suppress);
        Assert(keys.Decide(alt, false) == FocusKeyDecision.Pass);
    }
    var control = new FocusShortcutPolicy(); control.Decide(0xA2, true);
    Assert(control.Decide(0x1B, true) == FocusKeyDecision.Suppress && control.Decide(0x1B, false) == FocusKeyDecision.Suppress);
}));
await Test("Win L requests system lock once without injecting keyboard input", () => Check(() =>
{
    var keys = new FocusShortcutPolicy(); keys.Decide(0x5B, true);
    Assert(keys.Decide(0x4C, true) == FocusKeyDecision.LockWorkstation);
    Assert(keys.Decide(0x4C, true) == FocusKeyDecision.Suppress);
    Assert(keys.Decide(0x4C, false) == FocusKeyDecision.Suppress);
    Assert(keys.Decide(0x5B, false) == FocusKeyDecision.Suppress);
}));
await Test("Ctrl Alt Delete Ctrl Shift Escape and Ctrl Alt Escape remain available", () => Check(() =>
{
    foreach (var chord in new[] { new[] { 0xA2, 0xA4, 0x2E }, new[] { 0xA3, 0xA1, 0x1B }, new[] { 0xA2, 0xA5, 0x1B } })
    {
        var keys = new FocusShortcutPolicy();
        foreach (int key in chord) Assert(keys.Decide(key, true) == FocusKeyDecision.Pass);
        foreach (int key in chord.Reverse()) Assert(keys.Decide(key, false) == FocusKeyDecision.Pass);
    }
}));
await Test("modifiers already held when the hook starts are recognized and released normally", () => Check(() =>
{
    var keys = new FocusShortcutPolicy(); keys.SeedModifier(0xA4);
    Assert(keys.Decide(0x09, true) == FocusKeyDecision.Suppress && keys.Decide(0x09, false) == FocusKeyDecision.Suppress);
    Assert(keys.Decide(0xA4, false) == FocusKeyDecision.Pass && keys.Decide(0x09, true) == FocusKeyDecision.Pass);
}));
await Test("helper command line rejects invalid pipe names without entering normal app startup", async () =>
{
    Assert(await FocusShellWatchdog.TryRunAsync([]) is null);
    Assert(await FocusShellWatchdog.TryRunAsync(["--focus-shell-watchdog"]) == 2);
    Assert(await FocusShellWatchdog.TryRunAsync(["--focus-shell-watchdog", @"..\arbitrary"]) == 2);
    Assert(FocusShellWatchdog.ValidPipeName("FocusPlanet-Shell-" + Guid.NewGuid().ToString("N")));
});
await Test("real helper IPC with no shell handles expires after lost heartbeat", async () =>
{
    // The child has an empty taskbar snapshot: no input hook or shell visibility change is performed.
    using var lease = await FocusShellWatchdog.StartAsync([], CancellationToken.None);
    Assert(lease.IsHealthy); var elapsed = Stopwatch.StartNew();
    while (lease.IsHealthy && elapsed.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100);
    Assert(!lease.IsHealthy && elapsed.Elapsed >= TimeSpan.FromSeconds(7));
});
await Test("real helper IPC stays ready with heartbeats and stops on explicit disposal", async () =>
{
    var lease = await FocusShellWatchdog.StartAsync([], CancellationToken.None);
    try
    {
        for (int i = 0; i < 19; i++) { lease.Pulse(); await Task.Delay(500); Assert(lease.IsHealthy); }
    }
    finally { lease.Dispose(); }
    Assert(!lease.IsHealthy);
    await FocusShellWatchdog.WaitForPendingRestorationAsync(CancellationToken.None);
});
await Test("a new helper waits until the previous helper has finished restoration", async () =>
{
    var first = await FocusShellWatchdog.StartAsync([], CancellationToken.None); first.Dispose();
    using var second = await FocusShellWatchdog.StartAsync([], CancellationToken.None);
    Assert(second.IsHealthy); second.Pulse();
});
Console.WriteLine($"Workspace: {passed} passed, {failed} failed. Fake desktop and empty-snapshot helper only; no user input or taskbar visibility changed.");
return failed == 0 ? 0 : 1;

sealed class FakeRecovery : IFocusRecoveryLease
{
    public bool Healthy = true, Disposed;
    public bool IsHealthy => Healthy && !Disposed;
    public void Pulse() { if (!IsHealthy) throw new IOException("helper unavailable"); }
    public void Dispose() => Disposed = true;
}
sealed class FakeKeyboard : IFocusShortcutLease
{
    public bool Healthy = true, Disposed;
    public bool IsHealthy => Healthy && !Disposed;
    public void Dispose() => Disposed = true;
}
sealed class FakePlatform : IFocusWorkspacePlatform
{
    public List<string> Events = [];
    public List<FocusForegroundWindow> Windows = [new(100, 1, "focusgarden", false), new(200, 2, "word", false), new(300, 3, "notepad", false)];
    public FocusForegroundWindow? Foreground = new(200, 2, "word", false);
    public IReadOnlyList<FocusTaskbarWindow> Bars = [new(1, true), new(2, false)];
    public readonly FakeRecovery Recovery = new();
    public readonly FakeKeyboard Keyboard = new();
    public bool FailRecovery, FailKeyboard, FailMinimize;
    public int HideFailureHandle, RestoreFailureHandle;
    public long Now = 100;
    public Action? RecoveryReady;
    public Func<bool>? Active;
    public int ProcessId => 1;
    public long Timestamp => Now;
    public double ElapsedSeconds(long timestamp) => (Now - timestamp) / 1000.0;
    public IReadOnlyList<FocusTaskbarWindow> GetTaskbars() => Bars;
    public void SetTaskbarVisible(nint handle, bool visible)
    {
        Events.Add($"bar:{handle}:{visible}");
        if (!visible && handle == HideFailureHandle || visible && handle == RestoreFailureHandle) throw new IOException("synthetic visibility failure");
    }
    public FocusForegroundWindow? GetForeground() => Foreground;
    public IReadOnlyList<FocusForegroundWindow> GetWindows() => Windows;
    public bool IsWindow(nint handle) => Windows.Any(w => w.Handle == handle);
    public bool Minimize(nint handle) { Events.Add("minimize:" + handle); return !FailMinimize; }
    public bool Activate(nint handle) { Events.Add("activate:" + handle); return IsWindow(handle); }
    public Task<IFocusShortcutLease> InstallShortcutsAsync(Func<bool> isActive, CancellationToken token)
    {
        if (FailKeyboard) throw new IOException("synthetic keyboard failure");
        Active = isActive; Events.Add("keyboard-ready"); return Task.FromResult<IFocusShortcutLease>(Keyboard);
    }
    public Task<IFocusRecoveryLease> StartRecoveryAsync(IReadOnlyList<FocusTaskbarWindow> taskbars, CancellationToken token)
    {
        if (FailRecovery) throw new IOException("synthetic helper failure");
        Events.Add("recovery-ready"); RecoveryReady?.Invoke(); return Task.FromResult<IFocusRecoveryLease>(Recovery);
    }
}
