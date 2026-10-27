namespace FocusGarden.Core;

public sealed record AppUsageSample(string? AppName, double MonotonicSeconds, DateTimeOffset Timestamp,
    double IdleSeconds, bool Enabled = true, bool IsLocked = false);

/// <summary>Conservative sample accounting: both interval endpoints must observe the same active application.</summary>
public sealed class UsageAccumulator
{
    private AppUsageSample? previous;
    private readonly Dictionary<(string App, DateTime Day), double> fractions = new();
    public string StateText { get; private set; } = "等待首次前台采样";

    public IReadOnlyList<AppUsageCredit> Sample(AppUsageSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!double.IsFinite(sample.MonotonicSeconds) || !double.IsFinite(sample.IdleSeconds) || sample.IdleSeconds < 0)
            throw new ArgumentException("采样时间必须是有效数值。", nameof(sample));
        string? app = string.IsNullOrWhiteSpace(sample.AppName) ? null : UsageCalendar.NormalizeName(sample.AppName);
        sample = sample with { AppName = app };
        var old = previous;
        previous = sample;
        StateText = !sample.Enabled ? "已暂停统计" : sample.IsLocked ? "系统已锁定，暂停统计" : sample.IdleSeconds >= 60
            ? "超过 60 秒未操作，暂停统计" : app == null ? "前台应用暂不可读或属于系统画面" : "正在统计：" + app;
        if (!Eligible(sample) || old == null || !Eligible(old) || !string.Equals(old.AppName, app, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<AppUsageCredit>();
        var delta = sample.MonotonicSeconds - old.MonotonicSeconds;
        if (delta <= 0 || delta >= 3)
        {
            StateText = "检测到停顿，已跳过未知时段";
            return Array.Empty<AppUsageCredit>();
        }
        // Split first, then round within each application/day, so a new day cannot inherit yesterday's fractions.
        var result = new List<AppUsageCredit>();
        foreach (var part in UsageCalendar.Split(sample.Timestamp.AddSeconds(-delta), delta))
        {
            var key = (app!, part.Day);
            fractions.TryGetValue(key, out var rest);
            rest += part.Seconds;
            var whole = (int)Math.Floor(rest + 1e-9);
            fractions[key] = Math.Max(0, rest - whole);
            if (whole > 0) result.Add(new AppUsageCredit(app!, part.Day, whole));
        }
        // Fractions are <1 second and never become history; discard prior dates to keep a long-lived process bounded.
        var today = sample.Timestamp.ToLocalTime().Date;
        foreach (var key in fractions.Keys.Where(k => k.Day < today.AddDays(-1)).ToArray()) fractions.Remove(key);
        return result;
    }

    public void ResetBaseline() => previous = null;
    private static bool Eligible(AppUsageSample value) => value.Enabled && !value.IsLocked && value.IdleSeconds < 60 && value.AppName != null;
}
