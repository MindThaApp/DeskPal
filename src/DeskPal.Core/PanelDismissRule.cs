namespace DeskPal.Core;

// Decides whether the tray quick panel should close when it loses activation. Pressing, holding or dragging the
// floating Bloop window beside the card must never close it; a genuine switch to another window does.
public static class PanelDismissRule
{
    public const long SettleMs = 300;

    public static bool ShouldDismiss(IntPtr foreground, IntPtr card, IntPtr bloop, bool bloopInteracting, long msSinceBloopRelease)
    {
        if (foreground == card) return false;
        if (bloop != IntPtr.Zero && foreground == bloop) return false;
        if (bloopInteracting || msSinceBloopRelease < SettleMs) return false;
        return true;
    }

    // Bloop took the foreground and the interaction is over: hand it back to the card so Esc keeps working.
    public static bool ShouldReclaimFocus(IntPtr foreground, IntPtr bloop, bool bloopInteracting, long msSinceBloopRelease)
        => bloop != IntPtr.Zero && foreground == bloop && !bloopInteracting && msSinceBloopRelease >= SettleMs;
}
