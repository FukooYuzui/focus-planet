namespace FocusGarden.Core;

public sealed class FocusSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public bool Immersive { get; set; } = true;
    public bool StrictFocus { get; set; }
    public bool WhitelistEnabled { get; set; }
    public string[] AllowedApps { get; set; } = [];
    public bool HideTaskbar { get; set; } = true;
    public bool BlockDesktopShortcuts { get; set; } = true;
    public string[] BlockedApps { get; set; } = [];
    public string[] BlockedDomains { get; set; } = [];
}

public sealed record FocusSession(
    long Id,
    string Task,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int PlannedSeconds,
    int ElapsedSeconds,
    string Status);

public sealed record FocusStatisticsSnapshot(int Year, DateTime Today, Dictionary<string, int> Daily,
    int TodaySeconds, int CompletedCount, IReadOnlyList<FocusSession> Latest);
