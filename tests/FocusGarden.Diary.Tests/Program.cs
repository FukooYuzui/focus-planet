using System.Diagnostics;
using System.Text;
using FocusGarden.Core;

if (args.FirstOrDefault() == "exec")
{
    Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
    var input = await Console.In.ReadToEndAsync();
    if (input == "hang") { await Task.Delay(30000); return 0; }
    if (input == "fail") { Console.Error.WriteLine("synthetic credential must never appear in UI"); return 12; }
    if (input == "large") { Console.Write(new string('x', 450000)); return 0; }
    Console.Write("# 合成日记\n\n" + input); return 0;
}
if (args.Length == 3 && args[0] == "--live")
{
    var prompt = DiarySource.BuildPrompt(new(new DateOnly(2026,11,1), [new("合成验证：阅读第一章",1500,"completed")], [], "合成计划：教材 10%"), new("用户：阅读第一章。Codex：已给出章节摘要。",2,false,0));
    var result = await new CodexDiaryClient().GenerateAsync(args[1], prompt, CancellationToken.None);
    if (!result.Contains("2026-11-01") || !result.Contains("阅读")) throw new Exception("Live synthetic diary lacks supplied facts");
    await File.WriteAllTextAsync(args[2], result, new UTF8Encoding(false)); Console.WriteLine("PASS official Codex CLI generated a Markdown diary from synthetic facts only"); return 0;
}
int passed = 0, failed = 0;
var executable = Path.ChangeExtension(typeof(CodexDiaryClient).Assembly.Location.Replace("FocusGarden.Core.dll", "FocusGarden.Diary.Tests.dll"), ".exe");
async Task Run(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
await Run("native child receives literal Unicode via stdin and returns Markdown without a shell", async () =>
{
    var text = "中文 ` $() \" & | 全部是数据";
    var result = await new CodexDiaryClient().GenerateAsync(executable, text, CancellationToken.None);
    if (result != "# 合成日记\n\n" + text) throw new Exception("Child text differed");
});
await Run("failed child reports a usable error without leaking stderr or inventing a diary", async () =>
{
    try { await new CodexDiaryClient().GenerateAsync(executable, "fail", CancellationToken.None); throw new Exception("Expected failure"); }
    catch (IOException ex) { if (!ex.Message.Contains("12") || ex.Message.Contains("credential")) throw new Exception("Wrong failure message"); }
});
await Run("cancel kills only the diary child promptly and permits subsequent generation", async () =>
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)); var watch = Stopwatch.StartNew();
    try { await new CodexDiaryClient().GenerateAsync(executable, "hang", cancel.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
    if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("Cancellation was slow");
    if (!(await new CodexDiaryClient().GenerateAsync(executable,"retry",CancellationToken.None)).Contains("retry")) throw new Exception("Retry failed");
});
await Run("oversized child output is drained safely and rejected without deadlocking pipes", async () =>
{
    var watch = Stopwatch.StartNew(); bool rejected = false;
    try { await new CodexDiaryClient().GenerateAsync(executable, "large", CancellationToken.None); } catch(IOException) { rejected = true; }
    if (!rejected || watch.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("Output cap or draining failed");
});
Console.WriteLine($"Diary process: {passed} passed, {failed} failed."); return failed == 0 ? 0 : 1;
