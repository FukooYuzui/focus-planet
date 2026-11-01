using System.Text.Json;
using FocusGarden.Core;

internal static class DiaryCases
{
    internal static void Run(string root, Action<string, Action> run)
    {
        string Folder() { var p = Path.Combine(root, "diary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        void Check(bool value) { if (!value) throw new Exception("Diary assertion failed"); }
        void Reject(Action action) { bool caught = false; try { action(); } catch (IOException) { caught = true; } Check(caught); }
        var day = new DateOnly(2026, 11, 1);
        run("diary Markdown roundtrips UTF8 and saves a byte-exact prior version on user edits", () =>
        {
            var folder = Folder(); var store = new DiaryStore(folder); Check(!store.LoadPreferences().Automatic);
            var first = store.Save(day, "# 我的日记\n\n- 实验 **完成**\n", null);
            var before = File.ReadAllBytes(store.PathFor(day));
            var second = store.Save(day, first.Markdown + "补充内容", first.Revision);
            Check(new DiaryStore(folder).Read(day) == second);
            Check(File.ReadAllBytes(Directory.GetFiles(Path.Combine(store.DirectoryPath, "backups")).Single()).SequenceEqual(before));
            Check(store.ListDays().SequenceEqual(new[] { day }));
        });
        run("generated diary never overwrites a manual file and stale editor revisions cannot clobber external changes", () =>
        {
            var store = new DiaryStore(Folder()); var first = store.Save(day, "手写正文", null);
            var draft = store.SaveGenerated(day, "AI 草稿"); Check(draft.Contains("drafts") && File.ReadAllText(draft) == "AI 草稿");
            Check(store.Read(day) == first); File.WriteAllText(store.PathFor(day), "外部编辑");
            Reject(() => store.Save(day, "旧编辑器", first.Revision)); Check(File.ReadAllText(store.PathFor(day)) == "外部编辑");
        });
        run("concurrent generation keeps one original and preserves other results as separate drafts", () =>
        {
            var store = new DiaryStore(Folder()); var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 6, i => paths.Add(store.SaveGenerated(day, "# 版本 " + i)));
            Check(paths.Distinct().Count() == 6 && paths.Count(x => x == store.PathFor(day)) == 1);
            Check(paths.All(File.Exists));
        });
        run("failed diary replacement retains previous file and successful retry preserves the draft", () =>
        {
            var store = new DiaryStore(Folder()); var original = store.Save(day, "旧文", null);
            using (var held = new FileStream(store.PathFor(day), FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => store.Save(day, "新文", original.Revision));
            Check(store.Read(day) == original); Check(store.Save(day, "新文", original.Revision).Markdown == "新文");
        });
        run("daily diary queries use recorded day allocations across midnight without changing any stored seconds", () =>
        {
            using var store = new FocusStore(Path.Combine(Folder(), "focus.db"));
            var local = new DateTime(2026, 11, 2, 0, 0, 10); var end = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            var id = store.BeginSession("跨日工作", 60, end.AddSeconds(-30)); store.AddProgress(id, 30, end); store.EndSession(id, "emergency", end);
            var before = store.GetSessions().Single(); Check(store.GetDiaryFocus(day).Single().Seconds == 20); Check(store.GetDiaryFocus(day.AddDays(1)).Single().Seconds == 10);
            Check(store.GetSessions().Single() == before && store.GetDailyTotals(2026).Values.Sum() == 30);
        });
        run("Codex source filters local calendar day and excludes tools system prompts duplicates and credentials", () =>
        {
            var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions")); var lines = new List<string>();
            var local = day.ToDateTime(new TimeOnly(12, 0)); var time = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            string Msg(string type, string message, DateTimeOffset at) => JsonSerializer.Serialize(new { timestamp = at, type = "event_msg", payload = new { type, message } });
            lines.Add(Msg("user_message", "请完成阅读", time)); lines.Add(lines[0]); lines.Add(Msg("agent_message", "已完成阅读\napi_key=sk-testnotarealcredential12345", time.AddSeconds(1)));
            lines.Add(Msg("user_message", "昨天内容", time.AddDays(-1))); lines.Add(Msg("exec_command_end", "工具秘密", time));
            lines.Add(JsonSerializer.Serialize(new { timestamp = time, type = "response_item", payload = new { role = "system", content = "系统秘密" } }));
            lines.Add("invalid json"); File.WriteAllLines(Path.Combine(folder, "sessions", "synthetic.jsonl"), lines);
            var result = DiarySource.ReadCodex(folder, day);
            Check(result.Messages == 2 && result.ReadErrors == 1 && result.Text.Contains("已完成阅读") && !result.Text.Contains("昨天内容") && !result.Text.Contains("工具秘密") && !result.Text.Contains("系统秘密") && !result.Text.Contains("sk-test"));
        });
        run("Codex log reader skips oversized records and observes cancellation without saving transcripts", () =>
        {
            var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions"));
            var path = Path.Combine(folder, "sessions", "synthetic.jsonl"); File.WriteAllText(path, new string('x', 140000) + "\n");
            Check(DiarySource.ReadCodex(folder, day).Limited);
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool caught = false;
            try { DiarySource.ReadCodex(folder, day, cancel.Token); } catch (OperationCanceledException) { caught = true; }
            Check(caught && Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 1);
        });
        run("current Codex response_item text messages work without any legacy event messages", () =>
        {
            var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions"));
            var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
            var lines = new[] {
                JsonSerializer.Serialize(new { timestamp = time, type = "response_item", payload = new { type = "message", id = "user-id", role = "user", content = new[] { new { type = "input_text", text = "请整理第一章" }, new { type = "input_text", text = "补充要求" } } } }),
                JsonSerializer.Serialize(new { timestamp = time.AddSeconds(20), type = "response_item", payload = new { type = "message", id = "assistant-id", role = "assistant", status = "completed", phase = "final_answer", content = new[] { new { type = "output_text", text = "已给出章节摘要" } } } }) };
            File.WriteAllLines(Path.Combine(folder, "sessions", "current.jsonl"), lines);
            var result = DiarySource.ReadCodex(folder, day);
            Check(result.Messages == 2 && result.Text.Contains("请整理第一章\n补充要求") && result.Text.Contains("已给出章节摘要") && result.Diagnostics.Contains("提取 2 条"));
        });
        run("modern diary extraction excludes reasoning tool targets developer instructions and agent coordination", () =>
        {
            var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions"));
            var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
            string Row(object payload) => JsonSerializer.Serialize(new { timestamp = time, type = "response_item", payload });
            var lines = new[] {
                Row(new { type = "message", role = "developer", content = new[] { new { type = "input_text", text = "私有开发指令" } } }),
                Row(new { type = "message", role = "assistant", channel = "analysis", content = new[] { new { type = "output_text", text = "私有推理" } } }),
                Row(new { type = "message", role = "assistant", recipient = "tools.exec", content = new[] { new { type = "output_text", text = "工具调用" } } }),
                Row(new { type = "agent_message", author = "worker", recipient = "/root", content = new[] { new { type = "input_text", text = "内部代理消息" } } }),
                Row(new { type = "function_call_output", output = "工具返回内容" }),
                Row(new { type = "message", role = "assistant", phase = "commentary", content = new[] { new { type = "output_text", text = "可见进展" }, new { type = "encrypted_content", text = "隐藏密文" } } }),
                "null", "[]", Row(new { type = "message", role = "user", content = "unsupported" }) };
            File.WriteAllLines(Path.Combine(folder, "sessions", "privacy.jsonl"), lines);
            var result = DiarySource.ReadCodex(folder, day);
            Check(result.Messages == 1 && result.Text.Contains("可见进展") && !result.Text.Contains("私有") && !result.Text.Contains("工具") && !result.Text.Contains("内部") && !result.Text.Contains("隐藏"));
        });
        run("legacy and modern mirror records count once while later repeated user messages remain", () =>
        {
            var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions"));
            var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
            string Old(DateTimeOffset at) => JsonSerializer.Serialize(new { timestamp = at, type = "event_msg", payload = new { type = "user_message", message = "继续" } });
            string Current(DateTimeOffset at) => JsonSerializer.Serialize(new { timestamp = at, type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "继续" } } } });
            File.WriteAllLines(Path.Combine(folder, "sessions", "mixed.jsonl"), new[] { Old(time), Current(time.AddMilliseconds(300)), Old(time.AddSeconds(10)), Current(time.AddSeconds(10.5)), Old(time.AddSeconds(11)) });
            Check(DiarySource.ReadCodex(folder, day).Messages == 3);
        });
        run("modern messages obey local midnight and still redact credential text", () =>
        {
            var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "archived_sessions"));
            var midnight = day.ToDateTime(TimeOnly.MinValue); var time = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
            string Row(DateTimeOffset at, string text) => JsonSerializer.Serialize(new { timestamp = at.ToUniversalTime(), type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } } });
            File.WriteAllLines(Path.Combine(folder, "archived_sessions", "midnight.jsonl"), new[] { Row(time.AddSeconds(-1), "前一天"), Row(time, "当天工作\npassword=synthetic-secret"), Row(time.AddDays(1), "后一天") });
            var result = DiarySource.ReadCodex(folder, day);
            Check(result.Messages == 1 && result.Text.Contains("当天工作") && !result.Text.Contains("前一天") && !result.Text.Contains("后一天") && !result.Text.Contains("synthetic-secret"));
        });
        run("each cross-format mirror consumes only one counterpart in either emission order", () =>
        {
            foreach (var legacyFirst in new[] { true, false })
            foreach (var mirrorDelay in new[] { 0, 0.3 })
            {
                var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions"));
                var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
                string Row(bool old, double seconds) => old
                    ? JsonSerializer.Serialize(new { timestamp = time.AddSeconds(seconds), type = "event_msg", payload = new { type = "user_message", message = "继续" } })
                    : JsonSerializer.Serialize(new { timestamp = time.AddSeconds(seconds), type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "继续" } } } });
                File.WriteAllLines(Path.Combine(folder, "sessions", "mirrors.jsonl"), new[] { Row(legacyFirst, 0), Row(!legacyFirst, mirrorDelay), Row(!legacyFirst, 1) });
                Check(DiarySource.ReadCodex(folder, day).Messages == 2);
            }
        });
        run("missing source and empty day have distinct diagnostics instead of implying no Codex activity", () =>
        {
            var folder = Folder(); var missing = DiarySource.ReadCodex(folder, day); Check(missing.Messages == 0 && missing.Diagnostics.Contains("没有找到"));
            Directory.CreateDirectory(Path.Combine(folder, "sessions")); var empty = DiarySource.ReadCodex(folder, day);
            Check(empty.Messages == 0 && empty.Diagnostics.Contains("扫描 0 个文件") && !empty.Diagnostics.Contains("没有找到"));
        });
        run("batched same-text mirror pairs retain all unpaired candidates in both format orders", () =>
        {
            foreach (var legacyFirst in new[] { true, false })
            {
                var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, "sessions"));
                var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
                string Row(bool old, double seconds) => old
                    ? JsonSerializer.Serialize(new { timestamp = time.AddSeconds(seconds), type = "event_msg", payload = new { type = "user_message", message = "继续" } })
                    : JsonSerializer.Serialize(new { timestamp = time.AddSeconds(seconds), type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "继续" } } } });
                var first = Row(legacyFirst, 0);
                File.WriteAllLines(Path.Combine(folder, "sessions", "batched.jsonl"), new[] { first, first, Row(legacyFirst, .1), Row(!legacyFirst, .2), Row(!legacyFirst, .3) });
                Check(DiarySource.ReadCodex(folder, day).Messages == 2);
            }
        });
        run("diary prompt marks current goals and bounded evidence without treating plans as completed work", () =>
        {
            var facts = new DiaryDayFacts(day, [new("完成实验", 60, "completed")], [], "研究报告 30%");
            var prompt = DiarySource.BuildPrompt(facts, new("尝试失败，待继续", 1, true, 2));
            Check(prompt.Contains("不能当作当天新增进度") && prompt.Contains("不调用工具") && prompt.Contains("ReadErrors"));
            Check(DiarySource.LocalDraft(facts).Contains("1 分钟"));
        });
        run("diary preferences reopen independently and malformed settings are preserved instead of silently enabling automation", () =>
        {
            var folder = Folder(); var store = new DiaryStore(folder); var prefs = new DiaryPreferences(true, false, "C:\\synthetic\\codex.exe");
            store.SavePreferences(prefs); Check(new DiaryStore(folder).LoadPreferences() == prefs);
            var path = Path.Combine(store.DirectoryPath, "settings.json"); File.WriteAllText(path, "broken"); bool caught = false;
            try { store.LoadPreferences(); } catch (JsonException) { caught = true; } Check(caught && File.ReadAllText(path) == "broken");
        });
    }
}
