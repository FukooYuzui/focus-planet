using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FocusGarden.Core;

public sealed record DiaryEntry(DateOnly Day, string Markdown, string Revision);
public sealed record DiaryPreferences(bool Automatic = false, bool IncludeCodex = true, string? CodexExecutable = null);

/// <summary>Independent Markdown files. Never overwrite a user's edits during generation.</summary>
public sealed class DiaryStore
{
    public const int MaximumCharacters = 200000;
    private readonly object gate = new();
    public string DirectoryPath { get; }
    public DiaryStore(string dataDirectory) => DirectoryPath = Path.Combine(Path.GetFullPath(dataDirectory), "diaries");
    public string PathFor(DateOnly day) => Path.Combine(DirectoryPath, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public DiaryEntry? Read(DateOnly day)
    {
        lock (gate)
        {
            var path = PathFor(day);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumCharacters * 4L) throw new IOException("日记文件过大，原文已保留。");
            var bytes = File.ReadAllBytes(path);
            return new(day, new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'), Revision(bytes));
        }
    }
    public DateOnly[] ListDays() => !Directory.Exists(DirectoryPath) ? [] : Directory.EnumerateFiles(DirectoryPath, "*.md")
        .Select(Path.GetFileNameWithoutExtension).Where(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        .Select(x => DateOnly.ParseExact(x!, "yyyy-MM-dd", CultureInfo.InvariantCulture)).OrderDescending().ToArray();
    public DiaryEntry Save(DateOnly day, string markdown, string? expectedRevision)
    {
        lock (gate)
        {
            Validate(markdown); Directory.CreateDirectory(DirectoryPath);
            var existing = Read(day);
            if (existing?.Revision != expectedRevision) throw new IOException("日记已在其他位置更新，未覆盖原文。请先复制当前文字，再重新打开该日期。");
            var path = PathFor(day); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteNew(temp, markdown);
                if (existing == null) File.Move(temp, path, false);
                else
                {
                    var backups = Path.Combine(DirectoryPath, "backups"); Directory.CreateDirectory(backups);
                    File.Replace(temp, path, Path.Combine(backups, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".md"));
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return Read(day)!;
        }
    }
    public string SaveGenerated(DateOnly day, string markdown)
    {
        lock (gate)
        {
            Validate(markdown);
            if (Read(day) == null) { Save(day, markdown, null); return PathFor(day); }
            var drafts = Path.Combine(DirectoryPath, "drafts"); Directory.CreateDirectory(drafts);
            var path = Path.Combine(drafts, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".md");
            WriteNew(path, markdown); return path;
        }
    }
    public DiaryPreferences LoadPreferences()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<DiaryPreferences>(File.ReadAllText(path)) ?? throw new IOException("日记设置为空，请检查原文件。");
    }
    public void SavePreferences(DiaryPreferences preferences)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json"); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(preferences), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    private static void Validate(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > MaximumCharacters || markdown.Contains('\0')) throw new ArgumentException("日记最多 20 万字，不能包含空字符。");
    }
    private static void WriteNew(string path, string markdown)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new UTF8Encoding(false).GetBytes(markdown); stream.Write(bytes); stream.Flush(true);
    }
}
