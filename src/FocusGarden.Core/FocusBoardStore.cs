using System.Text.Json;

namespace FocusGarden.Core;

public sealed record CompletedFocusTask(string Key, string Title, DateTimeOffset CompletedAt);
public sealed record FocusBoardSnapshot(string Plans, IReadOnlyList<CompletedFocusTask> Completed)
{
    public IReadOnlyList<LongTermGoal> Goals { get; init; } = [];
    public string DisplayPlans => string.Join("\n\n", Goals.Where(x => !x.Archived).Select(x => "• " + x.Title + "\n  " + x.ProgressLabel)
        .Concat(Plans.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => "• " + x)));
}

/// <summary>Small, independent board. Call on the same serial queue as focus writes.</summary>
public sealed class FocusBoardStore
{
    public const int MaximumPlansLength = 10000;
    public const int MaximumCompleted = 100;
    private sealed class Document
    {
        public int Version { get; set; } = 2;
        public string Plans { get; set; } = "";
        public List<CompletedFocusTask> Completed { get; set; } = [];
        public List<LongTermGoal> Goals { get; set; } = [];
        public HashSet<string> AppliedProgressKeys { get; set; } = new(StringComparer.Ordinal);
    }
    private readonly string path;
    private readonly Document document;
    private byte[]? legacyBytes;
    public bool Dirty { get; private set; }
    public FocusBoardStore(string directory, IEnumerable<FocusSession> history)
    {
        path = Path.Combine(Path.GetFullPath(directory), "focus-board.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16777216) throw new InvalidDataException("计划清单文件过大，原文件已保留。");
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException("计划清单文件为空。");
            if (document.Version is not (1 or 2) || document.Plans == null || document.Plans.Length > MaximumPlansLength || document.Completed == null || document.Completed.Count > MaximumCompleted)
                throw new InvalidDataException("计划清单格式或版本不兼容，原文件已保留。");
            foreach (var item in document.Completed) Validate(item);
            if (document.Completed.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != document.Completed.Count)
                throw new InvalidDataException("完成记录存在重复标识，原文件已保留。");
            if (document.Version == 1)
            {
                legacyBytes = File.ReadAllBytes(path);
                var notes = new List<string>();
                document.Goals = [];
                foreach (var line in document.Plans.Split('\n'))
                {
                    var title = line.Trim();
                    if (title.Length is > 0 and <= 120 && document.Goals.Count < 100)
                        document.Goals.Add(new(Guid.NewGuid().ToString("N"), title, "percent", []));
                    else notes.Add(line);
                }
                document.Plans = string.Join("\n", notes);
                document.Version = 2; Dirty = true;
            }
            ValidateGoals();
        }
        else
        {
            document = new(); Dirty = true;
            foreach (var row in history.Where(x => x.Status == "completed" && x.EndedAt != null).OrderBy(x => x.EndedAt))
                AddCompleted(new("session:" + row.Id, row.Task, row.EndedAt!.Value));
        }
    }
    public FocusBoardSnapshot Snapshot() => new(document.Plans, document.Completed.OrderByDescending(x => x.CompletedAt).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray())
    { Goals = document.Goals.Select(x => x with { Parts = x.Parts.ToArray() }).ToArray() };
    public void SetPlans(string plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Length > MaximumPlansLength) throw new ArgumentException("长期计划最多 10000 字。");
        if (document.Plans == plans) return;
        document.Plans = plans; Dirty = true;
    }
    public void AddCompleted(CompletedFocusTask task, GoalContribution? contribution = null)
    {
        Validate(task);
        if (document.Completed.Any(x => x.Key == task.Key) || document.AppliedProgressKeys.Contains(task.Key)) return;
        if (contribution != null)
        {
            if (document.AppliedProgressKeys.Count >= 200000) throw new InvalidOperationException("任务进度档案已满，请先导出备份。");
            ValidateContribution(contribution);
            var goal = document.Goals.Single(x => x.Id == contribution.GoalId);
            string detail;
            LongTermGoal updated;
            if (goal.Mode == "parts")
            {
                var ids = contribution.PartIds.ToHashSet(StringComparer.Ordinal);
                detail = string.Join("、", goal.Parts.Where(x => ids.Contains(x.Id)).Select(x => x.Title));
                updated = goal with { Parts = goal.Parts.Select(x => ids.Contains(x.Id) ? x with { Completed = true } : x).ToArray() };
            }
            else { updated = goal with { Percent = goal.Percent + contribution.Percent }; detail = $"本次 +{contribution.Percent:0.##}%"; }
            task = task with { Title = $"{task.Title} · {goal.Title}：{detail}（累计 {updated.ProgressLabel}）" };
            Validate(task);
            document.Goals[document.Goals.FindIndex(x => x.Id == goal.Id)] = updated;
            document.AppliedProgressKeys.Add(task.Key);
        }
        document.Completed.Add(task);
        document.Completed = document.Completed.OrderByDescending(x => x.CompletedAt).ThenBy(x => x.Key, StringComparer.Ordinal).Take(MaximumCompleted).ToList();
        Dirty = true;
    }
    public LongTermGoal SaveGoal(GoalDraft draft)
    {
        var title = draft.Title.Trim();
        if (title.Length is < 1 or > 120 || draft.Mode is not ("parts" or "percent")) throw new ArgumentException("请填写 1–120 字的任务名称并选择进度方式。");
        var old = draft.Id == null ? null : document.Goals.SingleOrDefault(x => x.Id == draft.Id) ?? throw new ArgumentException("任务不存在。");
        if (old?.Archived == true) throw new ArgumentException("已归档任务不能修改，可新建任务。");
        if (old == null && (document.Goals.Count(x => !x.Archived) >= 100 || document.Goals.Count >= 10000)) throw new ArgumentException("进行中的任务或历史档案已满，请先归档旧任务或导出备份。");
        if (old != null && old.Progress > 0 && old.Mode != draft.Mode) throw new ArgumentException("已有进度的任务不能切换计量方式，原进度已保留。");
        GoalPart[] parts = [];
        if (draft.Mode == "parts")
        {
            var titles = draft.PartTitles.Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            if (titles.Length is < 1 or > 100 || titles.Any(x => x.Length > 80) || titles.Distinct(StringComparer.Ordinal).Count() != titles.Length)
                throw new ArgumentException("请填写 1–100 个不同的部分，每个名称最多 80 字。");
            if (old != null && old.Parts.Any(x => x.Completed && !titles.Contains(x.Title))) throw new ArgumentException("已完成的部分不能删除或改名，以免丢失进度。");
            parts = titles.Select(t => old?.Parts.FirstOrDefault(x => x.Title == t) ?? new GoalPart(Guid.NewGuid().ToString("N"), t)).ToArray();
        }
        var goal = new LongTermGoal(old?.Id ?? Guid.NewGuid().ToString("N"), title, draft.Mode, parts, old?.Percent ?? 0);
        if (old == null) document.Goals.Add(goal); else document.Goals[document.Goals.IndexOf(old)] = goal;
        Dirty = true; return goal with { Parts = goal.Parts.ToArray() };
    }
    public void ArchiveGoal(string id)
    {
        int index = document.Goals.FindIndex(x => x.Id == id);
        if (index < 0) throw new ArgumentException("任务不存在。");
        document.Goals[index] = document.Goals[index] with { Archived = true }; Dirty = true;
    }
    public LongTermGoal SaveGoalPersisted(GoalDraft draft)
    {
        var previous = document.Goals.ToList(); var dirty = Dirty;
        try { var goal = SaveGoal(draft); Flush(); return goal; }
        catch { document.Goals = previous; Dirty = dirty; throw; }
    }
    public void ArchiveGoalPersisted(string id)
    {
        var previous = document.Goals.ToList(); var dirty = Dirty;
        try { ArchiveGoal(id); Flush(); }
        catch { document.Goals = previous; Dirty = dirty; throw; }
    }
    public void ValidateContribution(GoalContribution contribution)
    {
        var goal = document.Goals.SingleOrDefault(x => x.Id == contribution.GoalId && !x.Archived) ?? throw new ArgumentException("所选任务已不存在或已归档，请重新选择。");
        if (goal.Mode == "parts")
        {
            if (contribution.Percent != 0 || contribution.PartIds == null || contribution.PartIds.Length == 0 || contribution.PartIds.Distinct().Count() != contribution.PartIds.Length ||
                contribution.PartIds.Any(id => !goal.Parts.Any(p => p.Id == id && !p.Completed)))
                throw new ArgumentException("请选择尚未完成的部分；已经完成的部分不能重复计入。");
        }
        else if (contribution.PartIds == null || contribution.PartIds.Length != 0 || contribution.Percent <= 0 || contribution.Percent * 100 != decimal.Truncate(contribution.Percent * 100) || contribution.Percent > 100 - goal.Percent)
            throw new ArgumentException($"本轮百分比需大于 0，最多 {100 - goal.Percent:0.##}%，并保留最多两位小数。");
    }
    private void ValidateGoals()
    {
        if (document.Goals == null || document.AppliedProgressKeys == null || document.Goals.Count > 10000 || document.AppliedProgressKeys.Count > 200000 || document.AppliedProgressKeys.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100) ||
            document.Goals.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 100 || string.IsNullOrWhiteSpace(x.Title) || x.Title.Length > 120 || x.Mode is not ("parts" or "percent") ||
                x.Percent < 0 || x.Percent > 100 || x.Percent * 100 != decimal.Truncate(x.Percent * 100) || x.Parts == null || x.Parts.Length > 100 || x.Mode == "parts" && (x.Parts.Length == 0 || x.Percent != 0) || x.Mode == "percent" && x.Parts.Length != 0 ||
                x.Parts.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 100 || string.IsNullOrWhiteSpace(p.Title) || p.Title.Length > 80) || x.Parts.Select(p => p.Id).Distinct().Count() != x.Parts.Length || x.Parts.Select(p => p.Title).Distinct().Count() != x.Parts.Length) ||
            document.Goals.Select(x => x.Id).Distinct().Count() != document.Goals.Count)
            throw new InvalidDataException("长期任务数据不完整，原文件已保留。");
    }
    private static void Validate(CompletedFocusTask? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 100 || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 10000)
            throw new InvalidDataException("完成记录格式不正确，原文件已保留。");
    }
    public void Flush()
    {
        if (!Dirty) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (legacyBytes != null)
            {
                var backup = Path.Combine(Path.GetDirectoryName(path)!, "focus-board.v1-" + Guid.NewGuid().ToString("N") + ".json");
                using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(legacyBytes); stream.Flush(true); legacyBytes = null;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".previous", true);
            else File.Move(temporary, path);
            Dirty = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
