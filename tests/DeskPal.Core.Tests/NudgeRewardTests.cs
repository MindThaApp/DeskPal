using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class NudgeRewardTests
{
    [Fact]
    public void Early_done_before_any_exercise_earns_no_points() => Assert.Equal(0, NudgeReward.Points(0, timed: true, snoozed: false));

    [Fact]
    public void Partial_completion_scales_the_base_points() => Assert.Equal(5, NudgeReward.Points(0.5, timed: true, snoozed: false));

    [Fact]
    public void Full_countdown_earns_base_plus_bonus() =>
        Assert.Equal(NudgeReward.BasePoints + NudgeReward.FullBonusPoints, NudgeReward.Points(1, timed: true, snoozed: false));

    [Fact]
    public void Untimed_reminders_earn_base_without_bonus() => Assert.Equal(NudgeReward.BasePoints, NudgeReward.Points(0, timed: false, snoozed: false));

    [Fact]
    public void Snoozed_reminders_earn_less()
    {
        Assert.Equal(8, NudgeReward.Points(1, timed: true, snoozed: true));
        Assert.Equal(5, NudgeReward.Points(1, timed: false, snoozed: true));
    }

    [Fact]
    public void Out_of_range_completion_is_clamped()
    {
        Assert.Equal(0, NudgeReward.Points(-3, timed: true, snoozed: false));
        Assert.Equal(15, NudgeReward.Points(9, timed: true, snoozed: false));
    }

    [Fact]
    public void Scheduler_credits_points_and_still_counts_early_done_for_the_goal()
    {
        var t = new FakeTime(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        var s = new NudgeScheduler(t, new DeskPalSettings());
        var ev = new List<SchedulerEvent>();
        s.Event += ev.Add;
        for (var i = 0; i < 40 && s.Current is null; i++) { t.Advance(TimeSpan.FromMinutes(1)); s.Tick(); }
        Assert.NotNull(s.Current);
        Assert.True(s.Current!.TimedSeconds > 0);
        s.Execute(new DoneCommand(null, 0.5));
        Assert.Equal(1, s.StretchesToday);
        Assert.Equal(5, s.CarePoints);
        Assert.Equal(5, ev.Last(e => e.Type == SchedulerEventType.NudgeDone).Points);
        Assert.Equal(5, s.Snapshot().CarePoints);
    }
}
