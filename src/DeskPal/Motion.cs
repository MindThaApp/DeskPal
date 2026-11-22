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

    // The in-app Motion choice (Settings > Appearance). Windows can still turn animations off whatever this says.
    public static volatile DeskPal.Core.MotionChoice UserMode = DeskPal.Core.MotionChoice.FollowWindows;

    public const int ReducedChromeMs = 120;

    public static bool Enabled => _enabled && UserMode != DeskPal.Core.MotionChoice.Off;

    // Duration for a chrome animation: zero when animations are off (Windows or the app), shorter when reduced,
    // otherwise capped at 333 ms.
    public static TimeSpan Ms(int ms) =>
        TimeSpan.FromMilliseconds(!Enabled ? 0 : Math.Min(ms, UserMode == DeskPal.Core.MotionChoice.Reduced ? ReducedChromeMs : MaxChromeMs));

    private static bool Read()
    {
        try { return Ui.AnimationsEnabled; } catch { return true; }
    }
}
