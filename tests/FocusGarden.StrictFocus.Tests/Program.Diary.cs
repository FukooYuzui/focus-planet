using System.Windows.Controls;
using System.IO;
using System.Text.Json;
using FocusGarden.App;
using FocusGarden.Core;

internal static partial class Program
{
    private static void RunDiaryCases()
    {
        Run("diary generation passes modern Codex user and assistant source into the summary without tool output", () =>
        {
            using var f = new Fixture(false); var day = Field<DateOnly>(f.Window, "diaryDay");
            var source = Path.Combine(f.DirectoryPath, "empty-codex", "sessions"); Directory.CreateDirectory(source);
            var local = day.ToDateTime(new TimeOnly(12, 0)); var time = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            var rows = new[] {
                JsonSerializer.Serialize(new { timestamp = time, type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "synthetic user task" } } } }),
                JsonSerializer.Serialize(new { timestamp = time.AddSeconds(5), type = "response_item", payload = new { type = "message", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = "synthetic assistant completion" } } } }),
                JsonSerializer.Serialize(new { timestamp = time, type = "response_item", payload = new { type = "function_call_output", output = "private tool output" } }) };
            File.WriteAllLines(Path.Combine(source, "current.jsonl"), rows);
            SetField(f.Window, "diaryPreferences", new DiaryPreferences(false, true, "C:\\synthetic\\codex.exe"));
            bool called = false;
            SetField(f.Window, "diaryGenerator", (Func<string, string, CancellationToken, Task<string>>)((_, prompt, _) =>
            {
                called = true; Assert(prompt.Contains("synthetic user task") && prompt.Contains("synthetic assistant completion") && prompt.Contains("\"Messages\":2") && !prompt.Contains("private tool output"));
                return Task.FromResult("# 合成日记\n\n工作摘要来自两条可见对话。");
            }));
            Call(f.Window, "GenerateDiaryAsync", day, false); Assert(called); Assert(new DiaryStore(f.DirectoryPath).Read(day)!.Markdown.Contains("两条")); f.AssertHistory();
        });
        Run("diary editor saves Markdown on close and prevents unsaved date navigation without changing focus history", () =>
        {
            using var f = new Fixture(false); Call(f.Window, "Page", "diary");
            var editor = Field<TextBox>(f.Window, "diaryEditor"); var day = Field<DateOnly>(f.Window, "diaryDay");
            editor.Text = "# 工作日记\n\n## 已完成\n\n- 阅读 **第一章**\n\n> 明天继续\n\n```text\n实验记录\n```";
            Field<DatePicker>(f.Window, "diaryDate").SelectedDate = day.AddDays(-1).ToDateTime(TimeOnly.MinValue);
            Equal(day, Field<DateOnly>(f.Window, "diaryDay"));
            Render(f.Window, "diary-editor.png", 1200, 1100);
            Field<FlowDocumentScrollViewer>(f.Window, "diaryPreview").Document = DiaryMarkdown.Render(editor.Text);
            var tabs = (TabControl)((TabItem)editor.Parent).Parent; tabs.SelectedIndex = 1;
            Render(f.Window, "diary-preview.png", 1200, 1100); Render(f.Window, "diary-narrow.png", 960, 1100);
            Call(f.Window, "CloseDiaryAsync");
            Equal(editor.Text, new DiaryStore(f.DirectoryPath).Read(day)!.Markdown); f.AssertHistory();
        });
        Run("automatic diary generates once per missing day while manual regeneration preserves edits as a separate draft", () =>
        {
            using var f = new Fixture(false); var day = Field<DateOnly>(f.Window, "diaryDay"); int calls = 0;
            SetField(f.Window, "diaryPreferences", new DiaryPreferences(true, false, "C:\\synthetic\\codex.exe"));
            SetField(f.Window, "diaryGenerator", (Func<string, string, CancellationToken, Task<string>>)((_, prompt, _) => { calls++; Assert(prompt.Contains("diary-data")); return Task.FromResult("# 合成日记\n\n已完成阅读。"); }));
            Call(f.Window, "GenerateDiaryAsync", day, true); Call(f.Window, "GenerateDiaryAsync", day, true); Equal(1, calls);
            var editor = Field<TextBox>(f.Window, "diaryEditor"); editor.Text += "\n手动补充"; Call(f.Window, "SaveDiaryAsync");
            var before = new DiaryStore(f.DirectoryPath).Read(day)!;
            Call(f.Window, "GenerateDiaryAsync", day, false); Equal(2, calls);
            Equal(before, new DiaryStore(f.DirectoryPath).Read(day)); Assert(Field<string?>(f.Window, "diaryPendingDraft") != null); f.AssertHistory();
        });
        Run("Codex failure or cancellation leaves diary retryable and focus mode keeps diary editing disabled", () =>
        {
            using var f = new Fixture(false); var day = Field<DateOnly>(f.Window, "diaryDay");
            SetField(f.Window, "diaryPreferences", new DiaryPreferences(false, false, "C:\\synthetic\\codex.exe"));
            SetField(f.Window, "diaryGenerator", (Func<string, string, CancellationToken, Task<string>>)((_, _, _) => Task.FromException<string>(new IOException("synthetic failure"))));
            Call(f.Window, "GenerateDiaryAsync", day, false); Assert(new DiaryStore(f.DirectoryPath).Read(day) == null); Assert(!Field<bool>(f.Window, "diaryBusy"));
            SetField(f.Window, "diaryGenerator", (Func<string, string, CancellationToken, Task<string>>)((_, _, _) => Task.FromException<string>(new OperationCanceledException())));
            Call(f.Window, "GenerateDiaryAsync", day, false); Assert(new DiaryStore(f.DirectoryPath).Read(day) == null);
            f.Start(); Call(f.Window, "UpdateDiaryUi"); Assert(Field<TextBox>(f.Window, "diaryEditor").IsReadOnly);
            Call(f.Window, "Finish", "emergency", "stop"); f.Drain(); f.AssertHistory();
        });
    }
}
