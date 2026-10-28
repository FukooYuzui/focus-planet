using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FocusGarden.App;

/// <summary>Invoked before normal application startup. It never opens an application data directory.</summary>
internal static class FocusShellWatchdog
{
    internal const string Argument = "--focus-shell-watchdog";
    private const string PipePrefix = "FocusPlanet-Shell-";
    private sealed record Snapshot(int ParentProcessId, long ParentStartedUtcTicks, FocusTaskbarWindow[] Taskbars);
    private static readonly object restorationGate = new();
    private static Task pendingRestoration = Task.CompletedTask;

    internal static async Task WaitForPendingRestorationAsync(CancellationToken token)
    {
        Task pending;
        lock (restorationGate) pending = pendingRestoration;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5), token); }
        catch (TimeoutException) { throw new InvalidOperationException("上轮桌面恢复仍在进行，请稍后再开始。"); }
    }

    private static void TrackRestoration(Process helper, IReadOnlyList<FocusTaskbarWindow> taskbars)
    {
        lock (restorationGate)
            pendingRestoration = Task.WhenAll(pendingRestoration, CompleteRestorationAsync(helper, taskbars));
    }

    private static async Task CompleteRestorationAsync(Process helper, IReadOnlyList<FocusTaskbarWindow> taskbars)
    {
        try
        {
            await helper.WaitForExitAsync().ConfigureAwait(false);
            await FocusShellNative.WaitForRestorationAsync(taskbars).ConfigureAwait(false);
        }
        finally { helper.Dispose(); }
    }

    internal static async Task<IFocusRecoveryLease> StartAsync(IReadOnlyList<FocusTaskbarWindow> taskbars, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await WaitForPendingRestorationAsync(token);
        var pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        Process? helper = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定恢复助手路径。");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // Framework-dependent development runs need their entry DLL; the shipped
                // single-file EXE always uses its own process path and never this branch.
                var entryDll = Path.Combine(AppContext.BaseDirectory, typeof(FocusShellWatchdog).Assembly.GetName().Name + ".dll");
                if (!File.Exists(entryDll)) throw new FileNotFoundException("无法找到恢复助手的入口程序集。", entryDll);
                start.ArgumentList.Add(entryDll);
            }
            start.ArgumentList.Add(Argument); start.ArgumentList.Add(pipeName);
            helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动桌面恢复助手。");
            await pipe.WaitForConnectionAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var parent = Process.GetCurrentProcess();
            var snapshot = new Snapshot(Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks, taskbars.ToArray());
            await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot)).WaitAsync(TimeSpan.FromSeconds(2), token);
            var ready = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            if (ready != "READY") throw new InvalidOperationException("桌面恢复助手未准备好，未隐藏任务栏。");
            return new RecoveryLease(pipe, helper, writer, reader, taskbars.ToArray());
        }
        catch
        {
            pipe.Dispose();
            // A failed READY wait can occur after the child received its snapshot. It must
            // finish restoring before another session takes a new visibility snapshot.
            if (helper is not null) TrackRestoration(helper, taskbars);
            throw;
        }
    }

    /// <returns>Null for ordinary startup; otherwise the helper's process exit code.</returns>
    internal static async Task<int?> TryRunAsync(string[] args)
    {
        if (!args.Contains(Argument, StringComparer.Ordinal)) return null;
        if (args.Length != 2 || args[0] != Argument || !ValidPipeName(args[1])) return 2;
        FocusTaskbarWindow[] snapshot = [];
        try
        {
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (first is null || first.Length > 8192) return 2;
            var state = JsonSerializer.Deserialize<Snapshot>(first);
            if (state is null || state.ParentProcessId <= 0 || state.Taskbars is null || state.Taskbars.Length > 32 ||
                state.Taskbars.Any(t => t.Handle == 0) || state.Taskbars.Select(t => t.Handle).Distinct().Count() != state.Taskbars.Length) return 2;
            using var parent = Process.GetProcessById(state.ParentProcessId);
            using var self = Process.GetCurrentProcess();
            if (parent.HasExited || parent.SessionId != self.SessionId || parent.StartTime.ToUniversalTime().Ticks != state.ParentStartedUtcTicks) return 2;
            // Restore accepts only actual taskbar HWNDs. Never show arbitrary windows from command line data.
            if (state.Taskbars.Any(t => !FocusShellNative.IsWindow((nint)t.Handle) ||
                FocusShellNative.ClassName((nint)t.Handle) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))) return 2;
            snapshot = state.Taskbars;
            await writer.WriteLineAsync("READY");
            var parentExit = parent.WaitForExitAsync();
            while (true)
            {
                using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(FocusWorkspaceGuard.HeartbeatTimeoutSeconds));
                var read = reader.ReadLineAsync(heartbeat.Token).AsTask();
                var completed = await Task.WhenAny(read, parentExit);
                if (completed == parentExit)
                {
                    heartbeat.Cancel();
                    try { await read; } catch (OperationCanceledException) { }
                    return 0;
                }
                var line = await read; // Timeout restores in finally, even if the WPF process is still hung.
                if (line is null) return 0;
                if (line != "P") return 2;
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch { return 3; }
        finally { FocusShellNative.Restore(snapshot); }
    }

    internal static bool ValidPipeName(string value) => value.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        value.Length == PipePrefix.Length + 32 && Guid.TryParseExact(value[PipePrefix.Length..], "N", out _);

    private sealed class RecoveryLease(NamedPipeServerStream pipe, Process helper, StreamWriter writer, StreamReader reader,
        FocusTaskbarWindow[] taskbars) : IFocusRecoveryLease
    {
        private Task pending = Task.CompletedTask;
        private long pendingAt;
        private int disposed;
        public bool IsHealthy
        {
            get
            {
                if (Volatile.Read(ref disposed) != 0 || pending.IsFaulted || pending.IsCanceled) return false;
                try { return pipe.IsConnected && !helper.HasExited && (pending.IsCompleted || Stopwatch.GetElapsedTime(pendingAt).TotalSeconds < 2); }
                catch (InvalidOperationException) { return false; }
            }
        }
        public void Pulse()
        {
            if (!IsHealthy) throw new InvalidOperationException("桌面恢复助手已停止，白名单保护已解除。");
            if (!pending.IsCompleted) return;
            pendingAt = Stopwatch.GetTimestamp();
            pending = writer.WriteLineAsync("P");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            pipe.Dispose(); // EOF instructs the helper to restore, without needing a live parent dispatcher.
            try { writer.Dispose(); } catch { }
            try { reader.Dispose(); } catch { }
            TrackRestoration(helper, taskbars);
        }
    }
}
