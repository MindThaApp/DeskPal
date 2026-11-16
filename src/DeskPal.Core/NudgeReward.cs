namespace DeskPal.Core;

// Care points for a finished reminder. Done always counts toward the daily goal; the points scale with how much
// of a timed exercise was done, a full countdown adds a bonus, and a reminder that was snoozed first earns less.
public static class NudgeReward
{
    public const int BasePoints = 10;
    public const int FullBonusPoints = 5;
    public const double SnoozedFactor = 0.5;

    public static int Points(double completion, bool timed, bool snoozed)
    {
        var share = timed ? Math.Clamp(double.IsNaN(completion) ? 0 : completion, 0, 1) : 1;
        var points = BasePoints * share + (timed && share >= 1 ? FullBonusPoints : 0);
        if (snoozed) points *= SnoozedFactor;
        return (int)Math.Round(points, MidpointRounding.AwayFromZero);
    }
}
