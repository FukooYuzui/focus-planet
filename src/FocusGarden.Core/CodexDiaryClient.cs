using System.Diagnostics;
using System.Text;

namespace FocusGarden.Core;

public sealed class CodexDiaryClient
{
    public static string? FindExecutable()
    {
        // Prefer a native EXE: never interpolate a prompt or executable into a shell command.
        var roots = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(x => !string.IsNullOrWhiteSpace(x));
        foreach (var root in roots)
        {
            var path = Path.Combine(root.Trim('"'), "codex.exe");
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        foreach (var path in new[] {
            Path.Combine(npm,"node_modules","@openai","codex-win32-x64","vendor","x86_64-pc-windows-msvc","bin","codex.exe"),
            Path.Combine(npm,"vendor","x86_64-pc-windows-msvc","codex","codex.exe") })
            if (File.Exists(path)) return path;
        return null;
    }
    public static ProcessStartInfo StartInfo(string executable, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请安装 Codex CLI，或选择有效的 codex.exe。");
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "exec", "--ignore-user-config", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never",
            "-c", "approval_policy=\"never\"", "-c", "features.shell_tool=false", "-c", "features.apps=false", "-c", "web_search=\"disabled\"", "-c", "project_doc_max_bytes=0", "-c", "model_reasoning_effort=\"low\"", "-" }) info.ArgumentList.Add(argument);
        return info;
    }
    public async Task<string> GenerateAsync(string executable, string prompt, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "FocusPlanet-Diary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = new Process { StartInfo = StartInfo(executable, work) };
        Task<string>? stdout = null, stderr = null;
        try
        {
            process.Start();
            stdout = ReadLimitedAsync(process.StandardOutput, timeout.Token);
            stderr = ReadLimitedAsync(process.StandardError, timeout.Token);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout).Trim(); await stderr; // Never persist or expose credential-bearing stderr.
            if (process.ExitCode != 0) throw new IOException($"Codex 未完成整理（退出码 {process.ExitCode}）。请检查 Codex CLI 登录、网络和可用额度后重试，原日记未改变。");
            if (string.IsNullOrWhiteSpace(output) || output.Length > DiaryStore.MaximumCharacters) throw new IOException("Codex 未返回有效日记，原文未改变。");
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("Codex 整理超过三分钟，已停止。可稍后重试，原日记未改变。"); }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            foreach (var task in new[] { stdout, stderr }) if (task != null) { try { await task; } catch (Exception) { /* Observe canceled/failed drains after killing this child. */ } }
            // No recursive deletion: leave unexpected files for inspection.
            try { Directory.Delete(work, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken token)
    {
        var builder = new StringBuilder(); var buffer = new char[4096]; int count; bool overflow = false;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (builder.Length + count > 400000) overflow = true;
            else if (!overflow) builder.Append(buffer, 0, count);
        }
        if (overflow) throw new IOException("Codex 输出超过限制，整理已停止。");
        return builder.ToString();
    }
}
