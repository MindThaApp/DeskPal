using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class NudgeAutoHideTests
{
    [Fact]
    public void HidesAfterTheChosenTimeWithNobodyThere()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 20);
        Assert.False(h.Expired(19_999));
        Assert.True(h.Expired(20_000));
        Assert.False(h.Expired(20_100)); // only once
        Assert.False(h.Running);
    }

    [Fact]
    public void TimedExerciseRunsFirst()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 10, timedSeconds: 30);
        Assert.False(h.Expired(39_999));
        Assert.Equal(1, h.Fraction(5_000));
        Assert.True(h.Expired(40_000));
    }

    [Fact]
    public void PauseHoldsTheCard()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 20);
        h.Pause(15_000);
        Assert.False(h.Expired(500_000));
        Assert.Equal(5_000, h.LeftMs(500_000));
    }

    [Fact]
    public void ResumeKeepsWhatWasLeft()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 20);
        h.Pause(4_000);
        h.Resume(100_000);
        Assert.False(h.Expired(115_999));
        Assert.True(h.Expired(116_000));
    }

    [Fact]
    public void ResumeGivesAtLeastFiveSeconds()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 20);
        h.Pause(19_500);
        h.Resume(30_000);
        Assert.False(h.Expired(34_999));
        Assert.True(h.Expired(35_000));
    }

    [Fact]
    public void ResumeNeverExceedsShortHideTime()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 2);
        h.Pause(1_900);
        h.Resume(10_000);
        Assert.True(h.Expired(12_000));
    }

    [Fact]
    public void StoppedNeverExpires()
    {
        var h = new NudgeAutoHide();
        h.Start(0, 10);
        h.Stop();
        Assert.False(h.Expired(60_000));
    }

    [Fact]
    public void UnknownSettingFallsBackToRecommended()
    {
        var s = new DeskPalSettings { SettingsVersion = DeskPalSettings.CurrentVersion, HideNudgeSeconds = 7 };
        Assert.True(s.Migrate());
        Assert.Equal(DeskPalSettings.RecommendedHideNudgeSeconds, s.HideNudgeSeconds);
    }

    [Fact]
    public void DefaultIsTwentySeconds() => Assert.Equal(20, new DeskPalSettings().HideNudgeSeconds);
}
