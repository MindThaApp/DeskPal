using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class StreakTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2); // a Monday

    private static DayRecord Day(int offset, int done = 5, int goal = 5, bool relaxed = false) => new()
    {
        Date = Mon.AddDays(offset),
        Stretches = done, EyeBreaks = done, Walks = done,
        GoalStretches = goal, GoalEyes = goal, GoalWalks = goal,
        Relaxed = relaxed,
    };

    [Fact]
    public void ConsecutiveMetDaysBuildARun()
    {
        var past = new[] { Day(0), Day(1), Day(2) };
        var (cur, best) = StreakMath.Compute(past, Day(3), StreakGoal.Stretch);
        Assert.Equal(4, cur);
        Assert.Equal(4, best);
    }

    [Fact]
    public void MissedDayResetsRunButKeepsBest()
    {
        var past = new[] { Day(0), Day(1), Day(2), Day(3, done: 1), Day(4) };
        var (cur, best) = StreakMath.Compute(past, Day(5, done: 0), StreakGoal.All);
        Assert.Equal(1, cur);
        Assert.Equal(3, best);
    }

    [Fact]
    public void UnmetTodayDoesNotBreakTheRun()
    {
        var (cur, _) = StreakMath.Compute(new[] { Day(0), Day(1) }, Day(2, done: 0), StreakGoal.Eyes);
        Assert.Equal(2, cur);
    }

    [Fact]
    public void RelaxedUnmetDaysAreSkippedButRelaxedMetDaysCount()
    {
        var past = new[] { Day(0), Day(1, done: 0, relaxed: true), Day(2, relaxed: true), Day(3) };
        var (cur, best) = StreakMath.Compute(past, null, StreakGoal.Walk);
        Assert.Equal(3, cur);
        Assert.Equal(3, best);
    }

    [Fact]
    public void DaysWithoutARecordAreSkipped()
    {
        var past = new[] { Day(0), Day(4), Day(5) }; // app was not running on days 1-3
        var (cur, _) = StreakMath.Compute(past, null, StreakGoal.All);
        Assert.Equal(3, cur);
    }

    [Fact]
    public void DaysWithoutThatGoalAreNeutral()
    {
        var noWalk = Day(1);
        noWalk.GoalWalks = 0;
        noWalk.Walks = 0;
        var (cur, _) = StreakMath.Compute(new[] { Day(0), noWalk, Day(2) }, null, StreakGoal.Walk);
        Assert.Equal(2, cur);
        var (all, _) = StreakMath.Compute(new[] { Day(0), noWalk, Day(2) }, null, StreakGoal.All);
        Assert.Equal(3, all);
    }

    [Fact]
    public void ShuffledAndDuplicateRecordsAreHandled()
    {
        var past = new[] { Day(2), Day(0), Day(1), Day(1) };
        Assert.Equal(3, StreakMath.Compute(past, null, StreakGoal.Stretch).Current);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 2026, 11, 2, 2026, 11, 2)]
    [InlineData(DayOfWeek.Monday, 2026, 11, 8, 2026, 11, 2)]
    [InlineData(DayOfWeek.Sunday, 2026, 11, 8, 2026, 11, 8)]
    [InlineData(DayOfWeek.Monday, 2027, 1, 1, 2026, 12, 28)]
    public void WeekStartRespectsFirstDay(DayOfWeek first, int y, int m, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), StreakMath.WeekStart(new DateOnly(y, m, d), first));

    [Fact]
    public void DateMathAcrossDstChangeStaysOnCalendarDays()
    {
        // 2026-03-29 is the EU spring-forward date; calendar arithmetic must not skip or repeat a day.
        var start = new DateOnly(2026, 3, 27);
        var past = Enumerable.Range(0, 5).Select(i => new DayRecord { Date = start.AddDays(i), Stretches = 1, GoalStretches = 1 }).ToList();
        Assert.Equal(5, StreakMath.Compute(past, null, StreakGoal.Stretch).Current);
    }

    [Fact]
    public void RecapCoversLastWeekOnly()
    {
        var past = new List<DayRecord> { Day(-8), Day(-7), Day(-6, done: 2), Day(-3), Day(-1), Day(0) };
        var recap = StreakMath.BuildRecap(past, Mon.AddDays(1), DayOfWeek.Monday);
        Assert.NotNull(recap);
        Assert.Equal(Mon.AddDays(-7), recap!.WeekStart);
        Assert.Equal(5 + 2 + 5 + 5, recap.Stretches);
        Assert.Equal(3, recap.StretchDays);
        Assert.Equal(3, recap.AllDays);
        Assert.Equal(2, recap.BestStreak); // Day(-8), Day(-7) then Day(-6) missed
    }

    [Fact]
    public void RecapIsNullWhenLastWeekHasNoRecords()
    {
        Assert.Null(StreakMath.BuildRecap(new[] { Day(0) }, Mon.AddDays(1), DayOfWeek.Monday));
    }

    [Fact]
    public void RecapIsDueOncePerWeek()
    {
        var book = new StreakBook();
        book.Archive(Day(-3));
        var today = Mon.AddDays(2);
        Assert.True(book.RecapDue(today, DayOfWeek.Monday));
        book.MarkRecapShown(today, DayOfWeek.Monday);
        Assert.False(book.RecapDue(Mon.AddDays(4), DayOfWeek.Monday));
        Assert.False(book.RecapDue(Mon.AddDays(6), DayOfWeek.Monday));
        book.Archive(Day(3));
        Assert.True(book.RecapDue(Mon.AddDays(7), DayOfWeek.Monday));
    }

    [Fact]
    public void BestIsKeptAfterHistoryRestartsAndNewBestIsReportedOnce()
    {
        var book = new StreakBook();
        for (var i = 0; i < 3; i++) book.Archive(Day(i));
        var s = book.Summarize(Day(3));
        Assert.Equal(4, s.All.Best);
        Assert.True(s.All.IsNewBest);
        book.AcknowledgeBests();
        Assert.False(book.Summarize(Day(3)).AnyNewBest);
        // Break the streak: current resets quietly, best stays.
        book.Archive(Day(3, done: 0));
        var after = book.Summarize(Day(4, done: 0));
        Assert.Equal(0, after.All.Current);
        Assert.Equal(4, after.All.Best);
        Assert.False(after.AnyNewBest);
    }

    [Fact]
    public void ArchiveNeverReplacesAnExistingDay()
    {
        var book = new StreakBook();
        book.Archive(Day(0, done: 5));
        book.Archive(Day(0, done: 0));
        Assert.Single(book.History);
        Assert.Equal(5, book.History[0].Stretches);
    }

    [Fact]
    public void OldStateFilesLoadWithEmptyStreaks()
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<DeskPalState>("{\"StretchesToday\":3,\"CarePoints\":40}")!;
        Assert.Equal(3, state.StretchesToday);
        Assert.Empty(state.Streaks.History);
        state.Streaks.Normalize();
        Assert.Equal(4, state.Streaks.Bests.Length);
    }

    [Fact]
    public void SchedulerArchivesFinishedDayAndMarksPause()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var settings = new DeskPalSettings();
        var state = new DeskPalState { CountersDate = new DateOnly(2026, 11, 1), StretchesToday = 4, EyeBreaksToday = 2 };
        var s = new NudgeScheduler(time, settings, state);
        var snap = s.Snapshot();
        var rec = Assert.Single(snap.Streaks.History);
        Assert.Equal(new DateOnly(2026, 11, 1), rec.Date);
        Assert.Equal(4, rec.Stretches);
        Assert.True(rec.Relaxed); // a Sunday is outside the default working days
    }

    [Fact]
    public void ManualPauseMarksTheDayRelaxedWhenItEnds()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var s = new NudgeScheduler(time, new DeskPalSettings());
        s.Execute(new PauseCommand(Minutes: 30));
        time.Advance(TimeSpan.FromHours(24));
        var rec = Assert.Single(s.Snapshot().Streaks.History);
        Assert.Equal(new DateOnly(2026, 11, 2), rec.Date);
        Assert.True(rec.Relaxed);
        Assert.Equal(0, s.Streaks.All.Current);
    }
}
