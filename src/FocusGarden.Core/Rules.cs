using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FocusGarden.Core;

public static partial class DomainRules
{
    /// <summary>Accept a hostname (Unicode is converted to IDNA). Do not accept URLs, ports, paths or wildcards.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0 || text.Contains('/') || text.Contains(':') || text.Contains('*') || text.Any(char.IsWhiteSpace))
            throw new ArgumentException("网站请填写域名，例如 bilibili.com；不要填写网址路径、端口或通配符。", nameof(value));
        string domain;
        try { domain = new IdnMapping().GetAscii(text).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("域名格式无效。", nameof(value)); }
        if (domain.Length > 253 || !domain.Contains('.') || IPAddress.TryParse(domain, out _) ||
            domain.Split('.').Any(label => label.Length is 0 or > 63 || !DomainLabel().IsMatch(label)))
            throw new ArgumentException("请填写有效的公网域名，例如 bilibili.com。", nameof(value));
        if (domain.EndsWith(".localhost", StringComparison.Ordinal) || domain.EndsWith(".local", StringComparison.Ordinal))
            throw new ArgumentException("不支持本地域名。", nameof(value));
        return domain;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex DomainLabel();
}

public static partial class AppRules
{
    // Keep recovery, shell and this application's own windows outside the app blocker.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "focusgarden", "专注星球", "focusplanet", "explorer", "taskmgr", "system", "idle", "winlogon", "csrss", "lsass", "services",
        "svchost", "smss", "dwm", "sihost", "fontdrvhost", "wininit", "logonui", "lockapp",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "applicationframehost",
        "consent", "securityhealthsystray", "systemsettings", "cmd", "powershell", "pwsh", "windowsterminal"
    };
    // These are shell/security/service hosts, not an individual work application.
    // A shared host name cannot grant access to all applications hosted inside it.
    private static readonly HashSet<string> NotSelectableForWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "system", "idle", "registry", "secure system", "winlogon", "csrss", "lsass", "services",
        "svchost", "smss", "dwm", "sihost", "fontdrvhost", "wininit", "logonui", "lockapp",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "applicationframehost",
        "consent", "securityhealthsystray", "runtimebroker", "dllhost", "conhost"
    };

    /// <summary>Return a process basename without .exe. Accept process names only, never executable paths.</summary>
    public static string Normalize(string value)
    {
        var name = NormalizeProcessName(value);
        if (Protected.Contains(name))
            throw new ArgumentException("不能限制系统、紧急恢复工具或番茄钟自身。", nameof(value));
        return name;
    }

    /// <summary>Normalize an explicitly selected work app. Unlike blacklist rules, terminals are not implicitly exempt.</summary>
    public static string NormalizeAllowed(string value)
    {
        var name = NormalizeProcessName(value);
        if (NotSelectableForWhitelist.Contains(name))
            throw new ArgumentException("白名单请选择具体工作应用，不能选择 Windows 桌面、系统服务或通用应用宿主。", nameof(value));
        return name;
    }

    internal static string NormalizeProcessName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length is 0 or > 128 || name is "." or ".." || !ProcessName().IsMatch(name))
            throw new ArgumentException("应用请填写进程名称，例如 notepad.exe；不要填写文件路径。", nameof(value));
        return name.ToLowerInvariant();
    }

    public static bool IsProtected(string name) => Protected.Contains(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);

    [GeneratedRegex("^[\\p{L}\\p{N}_ .()\\-]+$")]
    private static partial Regex ProcessName();
}
