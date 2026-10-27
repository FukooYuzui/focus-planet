using System.IO;
using System.Text.Json;

namespace FocusGarden.App;
internal sealed class DashboardPreferences
{
    public bool TrackApplications { get; set; } = true;
    public string? CodexHome { get; set; }
    public bool AutoRefreshTokens { get; set; } = true;
    public static DashboardPreferences Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "dashboard.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<DashboardPreferences>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string directory)
    {
        var path = Path.Combine(directory, "dashboard.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, true);
    }
    public string ResolveCodexHome()
    {
        var environmentPath = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(CodexHome) ? Path.GetFullPath(CodexHome)
            : !string.IsNullOrWhiteSpace(environmentPath) ? Path.GetFullPath(environmentPath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}
