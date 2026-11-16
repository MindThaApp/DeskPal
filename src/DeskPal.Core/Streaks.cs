namespace DeskPal.Core;

public enum StreakGoal { Stretch = 0, Eyes = 1, Walk = 2, All = 3 }

// One finished (or in-progress) day: what was done, what the goals were, and whether the day was "relaxed".
// A relaxed day (outside the working days, or paused by the user) can add to a streak when its goal was met,
// but never breaks one.
public sealed class DayRecord
{
    public DateOnly Date { get; set; }
    public int Stretches { get; set; }
    public int EyeBreaks { get; set; }
    public int Walks { get; set; }
    public int GoalStretches { get; set; }
    public int GoalEyes { get; set; }
    public int GoalWalks { get; set; }
    public bool Relaxed { get; set; }

    public int Done(StreakGoal g) => g switch { StreakGoal.Stretch => Stretches, StreakGoal.Eyes => EyeBreaks, _ => Walks };
    public int Goal(StreakGoal g) => g switch { StreakGoal.Stretch => GoalStretches, StreakGoal.Eyes => GoalEyes, _ => GoalWalks };

    // True when the goal exists and was reached; for All, every goal that exists was reached (and at least one exists).
    public bool Met(StreakGoal g)
    {
        if (g != StreakGoal.All) return Goal(g) > 0 && Done(g) >= Goal(g);
        var any = false;
        foreach (var k in Kinds)
        {
            if (Goal(k) <= 0) continue;
            any = true;
            if (Done(k) < Goal(k)) return false;
        }
        return any;
    }

    // No goal at all means the day says nothing about this streak.
    public bool Applies(StreakGoal g) => g != StreakGoal.All ? Goal(g) > 0 : Kinds.Any(k => Goal(k) > 0);

    internal static readonly StreakGoal[] Kinds = [StreakGoal.Stretch, StreakGoal.Eyes, StreakGoal.Walk];
}

public sealed record StreakInfo(int Current, int Best, bool IsNewBest);

public sealed record StreakSummary(StreakInfo Stretch, StreakInfo Eyes, StreakInfo Walk, StreakInfo All)
{
    public StreakInfo For(StreakGoal g) => g switch { StreakGoal.Stretch => Stretch, StreakGoal.Eyes => Eyes, StreakGoal.Walk => Walk, _ => All };
    public bool AnyNewBest => Stretch.IsNewBest || Eyes.IsNewBest || Walk.IsNewBest || All.IsNewBest;
}

public sealed record WeekRecap(
    DateOnly WeekStart, int Stretches, int EyeBreaks, int Walks,
    int StretchDays, int EyeDays, int WalkDays, int AllDays, int BestStreak)
{
    public int Done(StreakGoal g) => g switch { StreakGoal.Stretch => Stretches, StreakGoal.Eyes => EyeBreaks, _ => Walks };
    public int GoalDays(StreakGoal g) => g switch { StreakGoal.Stretch => StretchDays, StreakGoal.Eyes => EyeDays, _ => WalkDays };
}

// Pure streak and recap arithmetic over calendar dates (DateOnly only, so DST and time zones cannot shift a day).
public static class StreakMath
{
    // Run of met days ending at the last counted day. Days with no record (app not running) are skipped, as are
    // relaxed days that missed the goal and days without that goal. An unmet today never breaks the run.
    public static (int Current, int Best) Compute(IEnumerable<DayRecord> past, DayRecord? today, StreakGoal g)
    {
        int run = 0, best = 0;
        foreach (var d in Ordered(past, today))
        {
            var isToday = today is not null && d.Date == today.Date;
            if (!d.Applies(g)) continue;
            if (d.Met(g)) { run++; if (run > best) best = run; }
            else if (!d.Relaxed && !isToday) run = 0;
        }
        return (run, best);
    }

    public static DateOnly WeekStart(DateOnly day, DayOfWeek first)
    {
        var back = ((int)day.DayOfWeek - (int)first + 7) % 7;
        return day.AddDays(-back);
    }

    // Totals of the week before the one that contains `today`; null when no day of it was recorded.
    public static WeekRecap? BuildRecap(IEnumerable<DayRecord> past, DateOnly today, DayOfWeek first)
    {
        var thisWeek = WeekStart(today, first);
        var start = thisWeek.AddDays(-7);
        var days = Ordered(past, null).Where(d => d.Date >= start && d.Date < thisWeek).ToList();
        if (days.Count == 0) return null;
        var before = Ordered(past, null).Where(d => d.Date < thisWeek).ToList();
        var best = Compute(before, null, StreakGoal.All).Best;
        return new WeekRecap(start,
            days.Sum(d => d.Stretches), days.Sum(d => d.EyeBreaks), days.Sum(d => d.Walks),
            days.Count(d => d.Met(StreakGoal.Stretch)), days.Count(d => d.Met(StreakGoal.Eyes)),
            days.Count(d => d.Met(StreakGoal.Walk)), days.Count(d => d.Met(StreakGoal.All)), best);
    }

    private static List<DayRecord> Ordered(IEnumerable<DayRecord> past, DayRecord? today)
    {
        var byDate = new Dictionary<DateOnly, DayRecord>();
        foreach (var d in past) if (d is not null && d.Date != default) byDate[d.Date] = d;
        if (today is not null) byDate[today.Date] = today;
        var list = byDate.Values.ToList();
        list.Sort((a, b) => a.Date.CompareTo(b.Date));
        return list;
    }
}

// What is kept in state.json for streaks and the weekly recap. Additive: files from before this existed load empty.
public sealed class StreakBook
{
    public const int MaxDays = 800;

    public List<DayRecord> History { get; set; } = [];
    // Best streaks seen so far (indexed by StreakGoal) and the best already celebrated.
    public int[] Bests { get; set; } = new int[4];
    public int[] Celebrated { get; set; } = new int[4];
    // The day the user last paused by hand; that day never breaks a streak.
    public DateOnly PausedDate { get; set; }
    // The week (its first day) whose "last week" recap was already shown.
    public DateOnly RecapShownWeek { get; set; }

    public void Normalize()
    {
        History ??= [];
        if (Bests is null || Bests.Length != 4) Bests = Resize(Bests);
        if (Celebrated is null || Celebrated.Length != 4) Celebrated = Resize(Celebrated);
    }

    private static int[] Resize(int[]? src)
    {
        var a = new int[4];
        if (src is not null) Array.Copy(src, a, Math.Min(4, src.Length));
        return a;
    }

    public void Archive(DayRecord day)
    {
        if (day.Date == default || History.Any(d => d.Date == day.Date)) return;
        History.Add(day);
        History.Sort((a, b) => a.Date.CompareTo(b.Date));
        // Bests are kept separately, so only very old days drop out of the list.
        if (History.Count > MaxDays) History.RemoveRange(0, History.Count - MaxDays);
    }

    public StreakSummary Summarize(DayRecord today)
    {
        StreakInfo Info(StreakGoal g)
        {
            var (cur, best) = StreakMath.Compute(History, today, g);
            var i = (int)g;
            if (best > Bests[i]) Bests[i] = best;
            var isNew = cur >= 3 && cur == Bests[i] && Bests[i] > Celebrated[i];
            return new StreakInfo(cur, Bests[i], isNew);
        }
        return new StreakSummary(Info(StreakGoal.Stretch), Info(StreakGoal.Eyes), Info(StreakGoal.Walk), Info(StreakGoal.All));
    }

    public void AcknowledgeBests()
    {
        for (var i = 0; i < 4; i++) Celebrated[i] = Math.Max(Celebrated[i], Bests[i]);
    }

    public bool RecapDue(DateOnly today, DayOfWeek first) =>
        RecapShownWeek != StreakMath.WeekStart(today, first) && StreakMath.BuildRecap(History, today, first) is not null;

    public void MarkRecapShown(DateOnly today, DayOfWeek first) => RecapShownWeek = StreakMath.WeekStart(today, first);
}
