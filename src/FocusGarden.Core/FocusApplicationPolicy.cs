using System.Collections.ObjectModel;

namespace FocusGarden.Core;

/// <summary>An immutable application restriction snapshot for one focus session.
/// It identifies process basenames, not executable identity, and is not an operating-system security boundary.</summary>
public sealed class FocusApplicationPolicy
{
    private static readonly HashSet<string> SafetyApps = new(StringComparer.Ordinal)
    {
        "taskmgr", "logonui", "winlogon", "lockapp", "consent"
    };
    private readonly HashSet<string> allowed;
    private readonly HashSet<string> blocked;

    private FocusApplicationPolicy(FocusSettings settings, string[] allowedNames, string[] blockedNames)
    {
        WhitelistEnabled = settings.WhitelistEnabled;
        HideTaskbar = WhitelistEnabled && settings.HideTaskbar;
        BlockDesktopShortcuts = WhitelistEnabled && settings.BlockDesktopShortcuts;
        allowed = new(allowedNames, StringComparer.Ordinal);
        blocked = new(blockedNames, StringComparer.Ordinal);
        AllowedApps = Array.AsReadOnly(allowedNames);
    }

    public bool WhitelistEnabled { get; }
    public bool HideTaskbar { get; }
    public bool BlockDesktopShortcuts { get; }
    public ReadOnlyCollection<string> AllowedApps { get; }

    /// <summary>Validate before beginning focus; later settings mutations cannot change this session.</summary>
    public static FocusApplicationPolicy Create(FocusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var allowedNames = NormalizeAllowedApps(settings.AllowedApps, settings.WhitelistEnabled);
        if (settings.BlockedApps is null) throw new ArgumentException("限制列表不能为空值。", nameof(settings));
        if (settings.BlockedApps.Length > 100) throw new ArgumentException("限制列表过长。", nameof(settings));
        var blockedNames = settings.BlockedApps.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(AppRules.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        return new(settings, allowedNames, blockedNames);
    }

    internal static string[] NormalizeAllowedApps(string[] values, bool required)
    {
        if (values is null) throw new ArgumentException("白名单不能为空值。", nameof(values));
        if (values.Length > 100) throw new ArgumentException("白名单最多允许 100 个应用。", nameof(values));
        var names = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(AppRules.NormalizeAllowed)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (required && names.Length == 0) throw new ArgumentException("请至少选择一个白名单应用后再开始专注。", nameof(values));
        return names;
    }

    public bool IsAllowed(string processName, bool isOwnProcess = false)
    {
        if (isOwnProcess) return true;
        var name = AppRules.NormalizeProcessName(processName);
        if (SafetyApps.Contains(name)) return true;
        return WhitelistEnabled ? allowed.Contains(name) : !blocked.Contains(name);
    }

    public static bool IsSystemSafetyApp(string processName) =>
        SafetyApps.Contains(AppRules.NormalizeProcessName(processName));
}
