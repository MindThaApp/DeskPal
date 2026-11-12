using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class PanelDismissRuleTests
{
    private static readonly IntPtr Card = new(10), Bloop = new(20), Other = new(30);

    [Fact] public void ForegroundBloopNeverDismisses() => Assert.False(PanelDismissRule.ShouldDismiss(Bloop, Card, Bloop, false, 5000));
    [Fact] public void ForegroundCardNeverDismisses() => Assert.False(PanelDismissRule.ShouldDismiss(Card, Card, Bloop, false, 5000));
    [Fact] public void DuringDragNeverDismisses() => Assert.False(PanelDismissRule.ShouldDismiss(Other, Card, Bloop, true, 5000));
    [Fact] public void JustAfterReleaseDoesNotDismiss() => Assert.False(PanelDismissRule.ShouldDismiss(Other, Card, Bloop, false, 100));
    [Fact] public void OutsideClickDismisses() => Assert.True(PanelDismissRule.ShouldDismiss(Other, Card, Bloop, false, 5000));
    [Fact] public void NoForegroundWindowDismissesWhenIdle() => Assert.True(PanelDismissRule.ShouldDismiss(IntPtr.Zero, Card, Bloop, false, 5000));

    [Fact]
    public void ReclaimOnlyWhenBloopForegroundAndIdle()
    {
        Assert.True(PanelDismissRule.ShouldReclaimFocus(Bloop, Bloop, false, 400));
        Assert.False(PanelDismissRule.ShouldReclaimFocus(Bloop, Bloop, true, 400));
        Assert.False(PanelDismissRule.ShouldReclaimFocus(Bloop, Bloop, false, 100));
        Assert.False(PanelDismissRule.ShouldReclaimFocus(Other, Bloop, false, 400));
    }
}
