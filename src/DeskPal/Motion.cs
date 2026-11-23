using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
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
    public static TimeSpan Ms(int ms) => Capped(ms, MaxChromeMs);

    // Things that open, grow or move may run up to 1.3x the old duration so the settle is visible. Closes use Ms().
    public const double OpenScale = 1.3;
    public const int MaxOpenMs = 433;

    public static TimeSpan OpenMs(int ms) => Capped((int)Math.Round(ms * OpenScale), MaxOpenMs);

    private static TimeSpan Capped(int ms, int cap) =>
        TimeSpan.FromMilliseconds(!Enabled ? 0 : Math.Min(ms, UserMode == DeskPal.Core.MotionChoice.Reduced ? ReducedChromeMs : cap));

    // The one shared settle curve, cubic-bezier(.45,0,.1,1): peak ~30% of the time, settles to zero. Change it here only.
    // A KeySpline is a UI object, so each animation gets its own one on the calling UI thread.
    private static KeySpline SettleSpline() => new() { ControlPoint1 = new(.45, 0), ControlPoint2 = new(.1, 1) };

    // Animates a property from..to along the settle curve.
    public static DoubleAnimationUsingKeyFrames Settle(double from, double to, TimeSpan duration)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        a.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        a.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = duration, Value = to, KeySpline = SettleSpline() });
        return a;
    }

    private static bool Read()
    {
        try { return Ui.AnimationsEnabled; } catch { return true; }
    }
}
