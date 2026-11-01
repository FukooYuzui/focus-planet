using System.Text.Json;

namespace FocusGarden.Core;

public sealed record CompletedFocusTask(string Key, string Title, DateTimeOffset CompletedAt);
public sealed record FocusBoardSnapshot(string Plans, IReadOnlyList<CompletedFocusTask> Completed);

/// <summary>Small, independent board. Call on the same serial queue as focus writes.</summary>
public sealed class FocusBoardStore
{
    public const int MaximumPlansLength = 10000;
    public const int MaximumCompleted = 100;
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public string Plans { get; set; } = "";
        public List<CompletedFocusTask> Completed { get; set; } = [];
    }
    private readonly string path;
    private readonly Document document;
    public bool Dirty { get; private set; }
    public FocusBoardStore(string directory, IEnumerable<FocusSession> history)
    {
        path = Path.Combine(Path.GetFullPath(directory), "focus-board.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 4194304) throw new InvalidDataException("计划清单文件过大，原文件已保留。");
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException("计划清单文件为空。");
            if (document.Version != 1 || document.Plans == null || document.Plans.Length > MaximumPlansLength || document.Completed == null || document.Completed.Count > MaximumCompleted)
                throw new InvalidDataException("计划清单格式或版本不兼容，原文件已保留。");
            foreach (var item in document.Completed) Validate(item);
            if (document.Completed.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != document.Completed.Count)
                throw new InvalidDataException("完成记录存在重复标识，原文件已保留。");
        }
        else
        {
            document = new(); Dirty = true;
            foreach (var row in history.Where(x => x.Status == "completed" && x.EndedAt != null).OrderBy(x => x.EndedAt))
                AddCompleted(new("session:" + row.Id, row.Task, row.EndedAt!.Value));
        }
    }
    public FocusBoardSnapshot Snapshot() => new(document.Plans, document.Completed.OrderByDescending(x => x.CompletedAt).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray());
    public void SetPlans(string plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Length > MaximumPlansLength) throw new ArgumentException("长期计划最多 10000 字。");
        if (document.Plans == plans) return;
        document.Plans = plans; Dirty = true;
    }
    public void AddCompleted(CompletedFocusTask task)
    {
        Validate(task);
        if (document.Completed.Any(x => x.Key == task.Key)) return;
        document.Completed.Add(task);
        document.Completed = document.Completed.OrderByDescending(x => x.CompletedAt).ThenBy(x => x.Key, StringComparer.Ordinal).Take(MaximumCompleted).ToList();
        Dirty = true;
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
