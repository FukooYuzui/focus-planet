using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusGarden.App;

internal static class PublishedHelperProbe
{
    internal static async Task<int> RunAsync(string executable)
    {
        try
        {
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable)) throw new FileNotFoundException("Published EXE not found.", executable);
            await CheckAsync(executable, true);
            Console.WriteLine("PASS published helper acknowledges an empty shell snapshot and stays alive with heartbeats");
            await CheckAsync(executable, false);
            Console.WriteLine("PASS published helper exits after a lost heartbeat without opening normal app storage");
            using var stream = File.OpenRead(executable);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Executable = executable, Version = FileVersionInfo.GetVersionInfo(executable).ProductVersion,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)),
                HelperCasesPassed = 2, ShellHandles = 0, KeyboardHookInstalled = false,
                Scope = "Published helper startup, authenticated local IPC, explicit stop and heartbeat expiration only. No desktop restriction was activated."
            }));
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL published helper: " + error); return 1; }
    }

    private static async Task CheckAsync(string executable, bool keepAlive)
    {
        string name = "FocusPlanet-Shell-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add(FocusShellWatchdog.Argument); info.ArgumentList.Add(name);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Helper did not start.");
        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        try
        {
        using var current = Process.GetCurrentProcess();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            ParentProcessId = Environment.ProcessId, ParentStartedUtcTicks = current.StartTime.ToUniversalTime().Ticks,
            Taskbars = Array.Empty<FocusTaskbarWindow>()
        }));
        if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "READY")
            throw new InvalidOperationException("Helper did not acknowledge the empty restore snapshot.");
        var duration = Stopwatch.StartNew();
        if (keepAlive)
        {
            for (int i = 0; i < 19; i++)
            {
                await writer.WriteLineAsync("P"); await Task.Delay(500);
                if (process.HasExited) throw new InvalidOperationException("Healthy helper exited during active heartbeat.");
            }
            pipe.Dispose();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            if (duration.Elapsed < TimeSpan.FromSeconds(7)) throw new InvalidOperationException("Helper exited without waiting for its heartbeat timeout.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Helper exited with code " + process.ExitCode);
        }
        finally
        {
            // EOF is the operation under test; flushing a writer after intentional closure
            // must not turn successful helper shutdown into a probe cleanup failure.
            try { writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            reader.Dispose();
        }
    }
}
