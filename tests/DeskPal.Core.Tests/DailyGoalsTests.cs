using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class DailyGoalsTests
{
    [Fact]
    public void DefaultsFollowIntervalsAcrossWorkingHours()
    {
        var goals = DailyGoals.For(new DeskPalSettings());
        Assert.Equal(15, goals.Stretches);
        Assert.Equal(22, goals.EyeBreaks);
        Assert.Equal(5, goals.Walks);
    }

    [Fact]
    public void DisabledKindsHaveNoGoal()
    {
        var goals = DailyGoals.For(new DeskPalSettings { MovementEnabled = false, EyesEnabled = false });
        Assert.Equal(new DailyGoals(0, 0, 0), goals);
    }

    [Fact]
    public void WalkGoalIsOffWhenWalksAreOff()
    {
        Assert.Equal(0, DailyGoals.For(new DeskPalSettings { WalkAfterStretches = 0 }).Walks);
    }
}
