using System.Globalization;
using System.Text.Json;
using FocusGarden.Core;
using Microsoft.Data.Sqlite;

if (args.Length == 2 && args[0] == "--scan-local")
{
    var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "../qa/v03-codex-live"));
    Directory.CreateDirectory(output);
    using var importer = new CodexUsageImporter(Path.Combine(output, "codex-usage.db"));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var result = await importer.ImportAsync(args[1]);
    var report = new { result, Totals = importer.GetTotals(), CurrentYear = importer.GetDailyTotals(DateTime.Now.Year), ElapsedSeconds = stopwatch.Elapsed.TotalSeconds };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "scan-summary.json"), json);
    Console.WriteLine(json);
    return;
}

var passed = 0; var failed = 0;
var testRoot = Path.Combine(Path.GetTempPath(), "FocusGarden-Codex-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
DateTimeOffset local = new(new DateTime(2026, 10, 27, 12, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 27, 12, 0, 0)));

async Task Run(string name, Func<string, Task> body)
{
    var folder = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try { await body(folder); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Equal<T>(T expected, T actual)
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void True(bool condition) { if (!condition) throw new Exception("Expected true"); }
string Home(string f, string name = "home") { var p = Path.Combine(f, name); Directory.CreateDirectory(Path.Combine(p, "sessions")); return p; }
string Log(string h, string name = "one.jsonl", string dir = "sessions") { var p = Path.Combine(h, dir, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); return p; }
CodexUsageImporter Importer(string f) => new(Path.Combine(f, "usage.db"));
object Usage(long input, long output, long cached = 0, long reasoning = 0) => new { input_tokens = input, output_tokens = output, cached_input_tokens = cached, reasoning_output_tokens = reasoning, total_tokens = input + output };
string Event(int second, object total, object? last, DateTimeOffset? date = null) => JsonSerializer.Serialize(new { timestamp = (date ?? local).AddSeconds(second).ToString("O"), type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = total, last_token_usage = last } } }) + "\n";

await Run("basic daily total excludes cached and reasoning double count", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(100, 30, 90, 10), Usage(100, 30, 90, 10)));
    using var i = Importer(f); var r = await i.ImportAsync(h);
    Equal(130L, i.GetDailyTotals(2026)["2026-10-27"]); Equal(90L, i.GetTotals().CachedInputTokens); Equal(10L, i.GetTotals().ReasoningOutputTokens); Equal(1L, r.TotalEvents);
});
await Run("cumulative totals are not added repeatedly", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(100, 20), Usage(100, 20)) + Event(1, Usage(250, 30), Usage(150, 10)));
    using var i = Importer(f); await i.ImportAsync(h); Equal(280L, i.GetTotals().TotalTokens);
});
await Run("identical cumulative status events with different timestamps count once", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(100, 20), Usage(100, 20)) + Event(1, Usage(100, 20), Usage(100, 20)));
    using var i = Importer(f); await i.ImportAsync(h); Equal(120L, i.GetTotals().TotalTokens); Equal(1L, i.GetTotals().EventCount);
});
await Run("fork copies across session and archive files are globally deduplicated", async f => {
    var h = Home(f); var content = Event(0, Usage(100, 20), Usage(100, 20));
    File.WriteAllText(Log(h), content); File.WriteAllText(Log(h, "copy.jsonl", "archived_sessions"), content);
    using var i = Importer(f); await i.ImportAsync(h); Equal(120L, i.GetTotals().TotalTokens); Equal(1L, i.GetTotals().EventCount);
});
await Run("unchanged metadata avoids reimport", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10, 2), Usage(10, 2)));
    using var i = Importer(f); await i.ImportAsync(h); var r = await i.ImportAsync(h); Equal(1, r.FilesUnchanged); Equal(0, r.FilesScanned); Equal(0L, r.EventsImported);
});
await Run("appended tokens count only new events", async f => {
    var h = Home(f); var p = Log(h); File.WriteAllText(p, Event(0, Usage(10, 2), Usage(10, 2)));
    using var i = Importer(f); await i.ImportAsync(h); File.AppendAllText(p, Event(1, Usage(30, 3), Usage(20, 1))); await i.ImportAsync(h); Equal(33L, i.GetTotals().TotalTokens);
});
await Run("file truncation removes obsolete event references", async f => {
    var h = Home(f); var p = Log(h); var first = Event(0, Usage(10, 2), Usage(10, 2)); File.WriteAllText(p, first + Event(1, Usage(30, 3), Usage(20, 1)));
    using var i = Importer(f); await i.ImportAsync(h); File.WriteAllText(p, first); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("rewritten file replaces old events", async f => {
    var h = Home(f); var p = Log(h); File.WriteAllText(p, Event(0, Usage(10, 2), Usage(10, 2)));
    using var i = Importer(f); await i.ImportAsync(h); File.WriteAllText(p, Event(5, Usage(25, 3), Usage(25, 3))); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(2)); await i.ImportAsync(h); Equal(28L, i.GetTotals().TotalTokens);
});
await Run("moving session into archive preserves one event", async f => {
    var h = Home(f); var p = Log(h); File.WriteAllText(p, Event(0, Usage(10, 2), Usage(10, 2)));
    using var i = Importer(f); await i.ImportAsync(h); File.Move(p, Log(h, "moved.jsonl", "archived_sessions")); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("deleting one duplicate retains event and deleting all removes it", async f => {
    var h = Home(f); var a = Log(h); var b = Log(h, "copy.jsonl"); File.WriteAllText(a, Event(0, Usage(10, 2), Usage(10, 2))); File.Copy(a, b);
    using var i = Importer(f); await i.ImportAsync(h); File.Delete(a); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens); File.Delete(b); await i.ImportAsync(h); Equal(0L, i.GetTotals().TotalTokens);
});
await Run("counter reset credits last response only", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(100, 20), Usage(100, 20)) + Event(1, Usage(15, 2), Usage(15, 2)));
    using var i = Importer(f); await i.ImportAsync(h); Equal(137L, i.GetTotals().TotalTokens);
});
await Run("first cumulative snapshot does not invent missing history", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10000, 2000), Usage(10, 2)));
    using var i = Importer(f); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("missing last uses later positive delta but skips unknown initial history", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(100, 20), null) + Event(1, Usage(120, 25), null));
    using var i = Importer(f); await i.ImportAsync(h); Equal(25L, i.GetTotals().TotalTokens); Equal(1L, i.GetTotals().EventCount);
});
await Run("missing last after counter reset is not guessed", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(100, 20), Usage(100, 20)) + Event(1, Usage(10, 2), null));
    using var i = Importer(f); await i.ImportAsync(h); Equal(120L, i.GetTotals().TotalTokens);
});
await Run("timestamps allocate to local day across midnight and year", async f => {
    var h = Home(f); var midnight = new DateTimeOffset(new DateTime(2026, 1, 1), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 1)));
    File.WriteAllText(Log(h), Event(-1, Usage(10, 2), Usage(10, 2), midnight.ToUniversalTime()) + Event(1, Usage(20, 4), Usage(10, 2), midnight.ToUniversalTime()));
    using var i = Importer(f); await i.ImportAsync(h); Equal(12L, i.GetDailyTotals(2025)["2025-12-31"]); Equal(12L, i.GetDailyTotals(2026)["2026-01-01"]);
});
await Run("malformed and negative events are skipped", async f => {
    var h = Home(f); File.WriteAllText(Log(h), "{\"token_count\":\n" + Event(0, Usage(-1, 2), Usage(-1, 2)) + Event(1, Usage(10, 2), Usage(10, 2)));
    using var i = Importer(f); var r = await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens); True(r.SkippedRecords >= 2);
});
await Run("partial final event is imported after completion", async f => {
    var h = Home(f); var p = Log(h); var content = Event(0, Usage(10, 2), Usage(10, 2)); File.WriteAllText(p, content[..^12]);
    using var i = Importer(f); await i.ImportAsync(h); Equal(0L, i.GetTotals().TotalTokens); File.AppendAllText(p, content[^12..]); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("valid final line without newline is accepted", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10, 2), Usage(10, 2)).TrimEnd()); using var i = Importer(f); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("source change never combines two Codex homes", async f => {
    var h = Home(f); var other = Home(f, "other"); File.WriteAllText(Log(h), Event(0, Usage(10, 2), Usage(10, 2))); File.WriteAllText(Log(other), Event(1, Usage(100, 20), Usage(100, 20)));
    using var i = Importer(f); await i.ImportAsync(h); await i.ImportAsync(other); Equal(120L, i.GetTotals().TotalTokens); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("cache and active source survive reopen", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10, 2), Usage(10, 2))); using (var i = Importer(f)) await i.ImportAsync(h); using var reopened = Importer(f); Equal(12L, reopened.GetTotals().TotalTokens);
});
await Run("large counts use Int64", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(4_000_000_000L, 500), Usage(4_000_000_000L, 500))); using var i = Importer(f); await i.ImportAsync(h); Equal(4_000_000_500L, i.GetTotals().TotalTokens);
});
await Run("explicit zero usage is distinguishable from no record", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(0, 0), Usage(0, 0))); using var i = Importer(f); await i.ImportAsync(h); True(i.GetDailyTotals(2026).ContainsKey("2026-10-27")); Equal(0L, i.GetTotals().TotalTokens);
});
await Run("large non-token line is bounded and next event remains readable", async f => {
    var h = Home(f); File.WriteAllText(Log(h), new string('x', 2 * 1024 * 1024) + "\n" + Event(0, Usage(10, 2), Usage(10, 2))); using var i = Importer(f); await i.ImportAsync(h); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("cached and reasoning subtotals are clamped to their parent counts", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10, 2, 20, 30), Usage(10, 2, 20, 30))); using var i = Importer(f); await i.ImportAsync(h); Equal(10L, i.GetTotals().CachedInputTokens); Equal(2L, i.GetTotals().ReasoningOutputTokens); Equal(12L, i.GetTotals().TotalTokens);
});
await Run("empty valid home returns no usage", async f => {
    var h = Home(f); using var i = Importer(f); var r = await i.ImportAsync(h); Equal(0L, r.TotalEvents); Equal(0, r.DaysWithUsage); Equal(0, i.GetDailyTotals(2026).Count);
});
await Run("pre-cancelled import preserves existing totals", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10, 2), Usage(10, 2))); using var i = Importer(f); await i.ImportAsync(h);
    using var cts = new CancellationTokenSource(); cts.Cancel(); try { await i.ImportAsync(h, cts.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { } Equal(12L, i.GetTotals().TotalTokens);
});
await Run("local cache does not persist prompt text", async f => {
    var h = Home(f); const string sentinel = "PRIVATE_PROMPT_SENTINEL_SHOULD_NEVER_BE_STORED"; File.WriteAllText(Log(h), JsonSerializer.Serialize(new { type = "response_item", payload = new { content = sentinel } }) + "\n" + Event(0, Usage(10, 2), Usage(10, 2)));
    using (var i = Importer(f)) await i.ImportAsync(h);
    var bytes = File.ReadAllBytes(Path.Combine(f, "usage.db")); True(!System.Text.Encoding.UTF8.GetString(bytes).Contains(sentinel));
});
await Run("daily filter limits detailed totals to one local day", async f => {
    var h = Home(f); File.WriteAllText(Log(h), Event(0, Usage(10, 2), Usage(10, 2)) + Event(0, Usage(30, 3), Usage(20, 1), local.AddDays(1))); using var i = Importer(f); await i.ImportAsync(h); Equal(12L, i.GetTotals(local.Date).TotalTokens); Equal(21L, i.GetTotals(local.AddDays(1).Date).TotalTokens);
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
// Only this freshly created, exact temporary test directory is removed.
Directory.Delete(testRoot, true);
Environment.ExitCode = failed == 0 ? 0 : 1;
