namespace DeskPal.Core;

public enum IdleMove { Wave, Shrug, LookAround, Yawn, NodOff, Spin, MorningStretch }

// Picks Bloop's (or Pip's) next idle moment on Home from the local time and one random roll in [0, 1). Late at night
// he gets sleepy; the first idle moment of the morning is a big stretch; yawns grow more likely through the
// afternoon; a happy spin is a rare treat (about 1 in 25). Under reduced motion only the quiet face moves are picked.
public static class IdleLife
{
    public const double SpinChance = 0.04;

    public static bool IsNight(DateTime local) => local.Hour >= 23 || local.Hour < 6;

    public static bool IsMorning(DateTime local) => local.Hour is >= 6 and < 11;

    public static IdleMove Choose(DateTime local, double roll, bool motion, bool morningDone)
    {
        if (!motion) return roll < 0.5 ? IdleMove.Yawn : IdleMove.LookAround;
        if (IsNight(local)) return roll < 0.5 ? IdleMove.NodOff : roll < 0.8 ? IdleMove.Yawn : IdleMove.LookAround;
        if (IsMorning(local) && !morningDone) return IdleMove.MorningStretch;
        if (roll < SpinChance) return IdleMove.Spin;
        var yawn = local.Hour >= 14 ? 0.22 : 0.1;
        if (roll < SpinChance + 0.3) return IdleMove.LookAround;
        if (roll < SpinChance + 0.3 + yawn) return IdleMove.Yawn;
        return roll < (1 + SpinChance + 0.3 + yawn) / 2 ? IdleMove.Wave : IdleMove.Shrug;
    }
}
