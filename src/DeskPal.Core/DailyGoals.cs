namespace DeskPal.Core;

// Targets for the progress rings: how many of each break fit into the working day at the current intervals.
public readonly record struct DailyGoals(int Stretches, int EyeBreaks, int Walks)
{
    public static DailyGoals For(DeskPalSettings settings)
    {
        var hours = settings.WorkingHours;
        var minutes = Math.Max(0, (hours.End.ToTimeSpan() - hours.Start.ToTimeSpan()).TotalMinutes);
        var stretches = settings.MovementEnabled ? Math.Max(1, (int)(minutes / settings.MovementMinutes)) : 0;
        var eyes = settings.EyesEnabled ? Math.Max(1, (int)(minutes / settings.IntervalFor(NudgeKind.Eyes).TotalMinutes)) : 0;
        var walks = stretches > 0 && settings.WalkAfterStretches > 0 ? Math.Max(1, stretches / settings.WalkAfterStretches) : 0;
        return new DailyGoals(stretches, eyes, walks);
    }
}
