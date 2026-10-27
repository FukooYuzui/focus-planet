namespace FocusGarden.Core;

public sealed class FocusSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public bool Immersive { get; set; } = true;
    public bool StrictFocus { get; set; }
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
