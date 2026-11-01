using System.Globalization;
using System.Text.Json;

namespace FocusGarden.Core;

public sealed class DailyFocusSlot
{
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "12:00";
    public string Label { get; set; } = "学习";
    public string Apps { get; set; } = "";
    public string[] AppNames() => Apps.Split([';', '；', ',', '，', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(AppRules.NormalizeAllowed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record DailyFocusWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public string Key => Start.ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);
}

public sealed class DailyFocusPlan
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "22:00";
    public bool CountWholePeriod { get; set; }
    public bool AllowUnplannedApps { get; set; }
    public string? SuppressedWindow { get; set; }
    public List<DailyFocusSlot> Slots { get; set; } = [];
    public Dictionary<string, string> ApplicationPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static int Minute(string value)
    {
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException("时间请填写 24 小时制 HH:mm，例如 09:00 或 21:30。");
        return time.Hour * 60 + time.Minute;
    }
    public DailyFocusPlan Snapshot()
    {
        if (Version != 1) throw new InvalidDataException("全天计划版本不兼容，原计划已保留。");
        int start = Minute(Start), end = Minute(End), duration = (end - start + 1440) % 1440;
        if (duration == 0) throw new ArgumentException("每日开启和结束时间不能相同；结束较早时表示跨到次日。");
        if (Slots == null || Slots.Count > 48 || ApplicationPaths == null || ApplicationPaths.Count > 100)
            throw new ArgumentException("计划最多包含 48 段和 100 个应用。");
        var ranges = new List<(int Start, int End)>();
        var copy = new DailyFocusPlan { Enabled = Enabled, Start = Start, End = End, CountWholePeriod = CountWholePeriod,
            AllowUnplannedApps = AllowUnplannedApps, SuppressedWindow = SuppressedWindow };
        foreach (var slot in Slots)
        {
            if (slot == null || slot.Label == null || slot.Label.Length > 80 || slot.Apps == null || slot.Apps.Length > 5000)
                throw new ArgumentException("时段名称或应用列表过长。");
            int from = (Minute(slot.Start) - start + 1440) % 1440;
            int length = (Minute(slot.End) - Minute(slot.Start) + 1440) % 1440;
            if (length == 0 || from + length > duration) throw new ArgumentException("每个应用时段必须在每日开启至结束之间，且时长大于零。");
            ranges.Add((from, from + length));
            var apps = slot.AppNames();
            if (apps.Length > 100) throw new ArgumentException("每个时段最多允许 100 个应用。");
            copy.Slots.Add(new() { Start = slot.Start, End = slot.End, Label = slot.Label.Trim(), Apps = string.Join("; ", apps) });
        }
        var ordered = ranges.OrderBy(x => x.Start).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End) throw new ArgumentException("应用时段不能重叠；相邻时段可使用相同的交界时间。");
        foreach (var (name, path) in ApplicationPaths)
        {
            var normalized = AppRules.NormalizeAllowed(name);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                AppRules.NormalizeAllowed(Path.GetFileName(path)) != normalized)
                throw new ArgumentException("应用启动路径必须是对应的 EXE 文件，不可附带命令或参数。");
            copy.ApplicationPaths[normalized] = Path.GetFullPath(path);
        }
        return copy;
    }
    public DailyFocusWindow? WindowAt(DateTimeOffset now)
    {
        int start = Minute(Start), end = Minute(End);
        var date = now.Date;
        if (end < start && now.TimeOfDay.TotalMinutes < end) date = date.AddDays(-1);
        var from = new DateTimeOffset(date.AddMinutes(start), now.Offset);
        var until = new DateTimeOffset(date.AddDays(end < start ? 1 : 0).AddMinutes(end), now.Offset);
        return now >= from && now < until ? new(from, until) : null;
    }
    public DailyFocusSlot? SlotAt(DateTimeOffset now, DailyFocusWindow window)
    {
        var offset = (now - window.Start).TotalMinutes;
        int start = Minute(Start);
        return Slots.FirstOrDefault(slot =>
        {
            int from = (Minute(slot.Start) - start + 1440) % 1440;
            int length = (Minute(slot.End) - Minute(slot.Start) + 1440) % 1440;
            return offset >= from && offset < from + length;
        });
    }
}

public sealed class DailyFocusPlanStore(string directory)
{
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "daily-focus.json");
    public DailyFocusPlan Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 262144) throw new InvalidDataException("全天计划文件过大，原文件已保留。");
        return (JsonSerializer.Deserialize<DailyFocusPlan>(File.ReadAllText(path))
            ?? throw new InvalidDataException("全天计划为空。" )).Snapshot();
    }
    public void Save(DailyFocusPlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan.Snapshot(), new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".previous", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
