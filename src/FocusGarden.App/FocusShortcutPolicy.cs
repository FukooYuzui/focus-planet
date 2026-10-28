namespace FocusGarden.App;

internal enum FocusKeyDecision { Pass, Suppress, LockWorkstation }

/// <summary>Only shortcut state is held; no text, key history, or user input is recorded.</summary>
internal sealed class FocusShortcutPolicy
{
    private readonly HashSet<int> pressed = [];
    private readonly HashSet<int> suppressed = [];
    internal void SeedModifier(int key) => pressed.Add(key);
    internal FocusKeyDecision Decide(int key, bool down)
    {
        var repeat = pressed.Contains(key);
        if (!down)
        {
            pressed.Remove(key);
            return suppressed.Remove(key) ? FocusKeyDecision.Suppress : FocusKeyDecision.Pass;
        }
        pressed.Add(key);
        if (suppressed.Contains(key)) return FocusKeyDecision.Suppress;
        bool win = pressed.Contains(0x5B) || pressed.Contains(0x5C);
        bool alt = pressed.Contains(0x12) || pressed.Contains(0xA4) || pressed.Contains(0xA5);
        bool control = pressed.Contains(0x11) || pressed.Contains(0xA2) || pressed.Contains(0xA3);
        bool shift = pressed.Contains(0x10) || pressed.Contains(0xA0) || pressed.Contains(0xA1);
        // Secure-attention and the two established recovery shortcuts remain outside this filter.
        if (key == 0x2E && control && alt || key == 0x1B && control && (alt || shift))
            return FocusKeyDecision.Pass;
        if (win && key == 0x4C)
        {
            suppressed.Add(key);
            return repeat ? FocusKeyDecision.Suppress : FocusKeyDecision.LockWorkstation;
        }
        bool blocked = key is 0x5B or 0x5C || win ||
            alt && key is 0x09 or 0x1B or 0x73 or 0x20 ||
            control && key == 0x1B;
        if (!blocked) return FocusKeyDecision.Pass;
        suppressed.Add(key);
        return FocusKeyDecision.Suppress;
    }
}
