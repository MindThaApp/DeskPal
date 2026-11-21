using Windows.UI.ViewManagement;

namespace DeskPal;

// Windows "Animation effects" setting, read in one place. Chrome animations (fades, slides, tray frames) check
// Enabled and use Ms() for their duration, which is capped at 333 ms and becomes 0 (instant) when animations are off.
internal static class Motion
{
    public const int MaxChromeMs = 333;

    private static readonly UISettings Ui = new();
    private static volatile bool _enabled = Read();

    static Motion()
    {
        try { Ui.AnimationsEnabledChanged += (_, _) => _enabled = Read(); } catch { }
    }

    public static bool Enabled => _enabled;

    // Duration for a chrome animation: zero when the user turned animations off, otherwise capped at 333 ms.
    public static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(Enabled ? Math.Min(ms, MaxChromeMs) : 0);

    private static bool Read()
    {
        try { return Ui.AnimationsEnabled; } catch { return true; }
    }
}
