using System.Collections.ObjectModel;

namespace FocusGarden.Core;

public readonly record struct PlanetDailyPoints(int FocusPoints, int TokenPoints, int SynergyPoints)
{
    public int TotalGrowth => FocusPoints + TokenPoints + SynergyPoints;
}

/// <summary>
/// TodayFocus/Token/Synergy/Growth describe the shared daily rewards. TodayPlanetGrowth is the share
/// awarded to this planet; TotalGrowth never includes another planet's credits.
/// </summary>
public sealed record PlanetSnapshot(
    long PlanetId,
    string Name,
    DateTimeOffset ActivatedAt,
    DateTimeOffset CycleEndsAt,
    bool CanExplore,
    DateTimeOffset? NextLevelAvailableAt,
    int AgeDays,
    long TotalGrowth,
    int Level,
    string LevelName,
    long? NextLevelAt,
    int TodayFocusPoints,
    int TodayTokenPoints,
    int TodaySynergyPoints,
    int TodayGrowth,
    int TodayPlanetGrowth,
    int TodayFocusSeconds,
    long TodayTokens,
    long NewGrowth,
    string? LatestUnlock,
    string Weather,
    bool IsNight,
    int Seed);

public sealed record PlanetSummary(long PlanetId, string Name, DateTimeOffset ActivatedAt,
    long TotalGrowth, bool IsActive);

/// <summary>Permanent, non-punitive growth with diminishing Token rewards and no daily reward cap.</summary>
public static class PlanetGrowthRules
{
    public static ReadOnlyCollection<long> LevelThresholds { get; } =
        Array.AsReadOnly(new long[] { 0, 150, 1200, 3000, 5500, 9000 });
    public static ReadOnlyCollection<int> MinimumAgeMonths { get; } =
        Array.AsReadOnly(new[] { 0, 0, 1, 2, 3, 5 });

    private static readonly string[] Names =
        ["初生星球", "森林萌芽", "生命栖息", "定居家园", "风能文明", "星际绿洲"];
    private static readonly string[] Unlocks =
        ["海洋、陆地与岩石", "树木与花朵", "小动物", "房屋与道路", "风车与高塔", "天文台与极光"];

    public static PlanetDailyPoints Calculate(int focusSeconds, long tokens)
    {
        var focus = Math.Max(0, focusSeconds) / 60;
        var token = (int)Math.Floor(15 * Math.Log2(1 + Math.Max(0, tokens) / 100000.0));
        var synergy = Math.Min(focus / 3, token);
        return new PlanetDailyPoints(focus, token, synergy);
    }

    public static int GetLevel(long totalGrowth)
    {
        for (var level = LevelThresholds.Count - 1; level > 0; level--)
            if (totalGrowth >= LevelThresholds[level]) return level;
        return 0;
    }

    public static int GetLevel(long totalGrowth, DateTimeOffset activatedAt, DateTimeOffset now)
    {
        var level = GetLevel(totalGrowth);
        while (level > 0 && now < activatedAt.AddMonths(MinimumAgeMonths[level])) level--;
        return level;
    }

    public static string GetLevelName(int level) => Names[ValidateLevel(level)];
    public static string GetUnlock(int level) => Unlocks[ValidateLevel(level)];

    internal static (string Weather, bool IsNight) GetWeather(int seed, DateTimeOffset now, int level)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var night = local.Hour >= 19 || local.Hour < 6;
        // A stable integer hash avoids runtime-dependent string hashing and random weather on refresh.
        var slot = (uint)(DateOnly.FromDateTime(local.Date).DayNumber * 8 + local.Hour / 3);
        var hash = unchecked((uint)seed ^ (slot * 0x9e3779b9u));
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x85ebca6bu);
        hash ^= hash >> 13;
        hash = unchecked(hash * 0xc2b2ae35u);
        hash ^= hash >> 16;
        var weather = level >= 5 && night && hash % 5 == 0
            ? "aurora"
            : hash % 10 < 6 ? "clear" : hash % 10 < 9 ? "cloudy" : "rain";
        return (weather, night);
    }

    private static int ValidateLevel(int level) => level is >= 0 and <= 5
        ? level : throw new ArgumentOutOfRangeException(nameof(level));
}
