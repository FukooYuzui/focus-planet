using FocusGarden.Core;

internal static class FocusBoardCases
{
    internal static void Run(string root, Action<string, Action> run)
    {
        string Folder() { var p = Path.Combine(root, "board-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        void Check(bool value) { if (!value) throw new Exception("Board assertion failed"); }
        var now = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8));
        run("focus board imports only completed history once without changing focus rows", () =>
        {
            var path = Folder(); using var db = new FocusStore(Path.Combine(path, "focus.db"));
            var a = db.BeginSession("完成阅读", 1, now); db.AddProgress(a, 1, now.AddSeconds(1)); db.EndSession(a, "completed", now.AddSeconds(1));
            var b = db.BeginSession("提前退出", 10, now); db.EndSession(b, "emergency", now);
            var before = db.GetSessions(); var board = new FocusBoardStore(path, db.GetCompletedSessions()); board.Flush();
            Check(board.Snapshot().Completed.Single().Title == "完成阅读"); Check(before.SequenceEqual(db.GetSessions()));
            var reopened = new FocusBoardStore(path, []); Check(reopened.Snapshot().Completed.Count == 1);
        });
        run("focus board preserves multiline plans unicode and stable completion identity across reopen", () =>
        {
            var path = Folder(); var board = new FocusBoardStore(path, []);
            board.SetPlans("读完量子力学\n每周运动三次 🌱"); board.AddCompleted(new("one", "第一章", now)); board.AddCompleted(new("one", "第一章", now)); board.Flush();
            var saved = new FocusBoardStore(path, []).Snapshot(); Check(saved.Plans == board.Snapshot().Plans && saved.Completed.Count == 1);
            board.SetPlans("新计划"); board.Flush(); Check(File.Exists(Path.Combine(path,"focus-board.json.previous")));
        });
        run("focus board retains the newest hundred tasks with bounded display work", () =>
        {
            var board = new FocusBoardStore(Folder(), []);
            for (int i = 0; i < 120; i++) board.AddCompleted(new("task:"+i, "任务"+i, now.AddMinutes(i)));
            var items = board.Snapshot().Completed; Check(items.Count == 100 && items[0].Title == "任务119" && items[^1].Title == "任务20");
        });
        run("focus board refuses corrupt or future documents without replacing bytes", () =>
        {
            foreach (var text in new[] { "not json", "{\"Version\":99}" })
            {
                var path = Folder(); var file = Path.Combine(path, "focus-board.json"); File.WriteAllText(file,text);
                bool threw=false; try { _ = new FocusBoardStore(path, []); } catch { threw=true; }
                Check(threw && File.ReadAllText(file)==text);
            }
        });
        run("focus board keeps failed writes retryable and preserves the previous saved file", () =>
        {
            var path=Folder(); var board=new FocusBoardStore(path, []); board.SetPlans("原计划"); board.Flush();
            var file=Path.Combine(path,"focus-board.json"); board.SetPlans("待保存计划");
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            { bool threw=false; try { board.Flush(); } catch(IOException) { threw=true; } Check(threw && board.Dirty); }
            Check(new FocusBoardStore(path, []).Snapshot().Plans == "原计划"); board.Flush();
            Check(!board.Dirty && new FocusBoardStore(path, []).Snapshot().Plans == "待保存计划");
        });
    }
}
