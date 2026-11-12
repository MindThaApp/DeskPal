using System.Numerics;
using DeskPal.Core;

namespace DeskPal.Core.Tests;

public class GestureTests
{
    private static GestureEvent? Tap(GestureClassifier g, Vector2 p, double t)
    {
        g.Down(p, t);
        return g.Up(p, t + 0.08);
    }

    [Fact]
    public void QuickPressIsATap()
    {
        var e = Tap(new GestureClassifier(), new(10, 10), 0);
        Assert.Equal(GestureKind.Tap, e?.Kind);
        Assert.Equal(1, e?.Count);
    }

    [Fact]
    public void TapsWithinTheGapEscalateAndEveryTapReports()
    {
        var g = new GestureClassifier();
        for (var i = 0; i < 5; i++)
        {
            var e = Tap(g, new(10 + i, 10), i * 0.25);
            Assert.NotNull(e);
            Assert.Equal(i + 1, e!.Value.Count);
            Assert.Equal(i == 0 ? GestureKind.Tap : GestureKind.MultiTap, e.Value.Kind);
        }
    }

    [Fact]
    public void TapAfterTheGapStartsOver()
    {
        var g = new GestureClassifier();
        Tap(g, new(10, 10), 0);
        Assert.Equal(1, Tap(g, new(10, 10), 0.08 + GestureClassifier.MultiTapGapSeconds + 0.05)?.Count);
    }

    [Fact]
    public void TapFarFromTheLastOneStartsOver()
    {
        var g = new GestureClassifier();
        Tap(g, new(10, 10), 0);
        Assert.Equal(1, Tap(g, new(200, 10), 0.2)?.Count);
    }

    [Fact]
    public void HoldingStillBecomesALongPressWithProgress()
    {
        var g = new GestureClassifier();
        g.Down(new(5, 5), 0);
        Assert.Null(g.Tick(0.3));
        Assert.Equal(GestureKind.LongPressStart, g.Tick(0.5)?.Kind);
        var holding = g.Tick(0.45 + GestureClassifier.HoldRampSeconds / 2);
        Assert.Equal(GestureKind.Holding, holding?.Kind);
        Assert.InRange(holding!.Value.Progress, 0.45, 0.55);
        var end = g.Up(new(6, 5), 2.5);
        Assert.Equal(GestureKind.LongPressEnd, end?.Kind);
        Assert.Equal(2.5, end!.Value.Duration, 3);
        Assert.Equal(1, end.Value.Progress, 3);
    }

    [Fact]
    public void LongPressDoesNotCountTowardMultiTap()
    {
        var g = new GestureClassifier();
        Tap(g, new(10, 10), 0);
        g.Down(new(10, 10), 0.2);
        g.Tick(0.8);
        g.Up(new(10, 10), 0.9);
        Assert.Equal(1, Tap(g, new(10, 10), 1.0)?.Count);
    }

    [Fact]
    public void FastStrokeIsASwipeWithDirection()
    {
        var g = new GestureClassifier();
        g.Down(new(0, 50), 0);
        GestureEvent? last = null;
        for (var i = 1; i <= 6; i++) last = g.Move(new(i * 15, 50), i * 0.016);
        Assert.Equal(GestureKind.Drag, last?.Kind);
        var e = g.Up(new(100, 50), 0.11);
        Assert.Equal(GestureKind.Swipe, e?.Kind);
        Assert.True(e!.Value.Direction.X > 0.95f);
        Assert.True(e.Value.Speed > 450);
    }

    [Fact]
    public void UpwardSwipePointsUpInScreenSpace()
    {
        var g = new GestureClassifier();
        g.Down(new(50, 100), 0);
        for (var i = 1; i <= 5; i++) g.Move(new(50, 100 - i * 15), i * 0.016);
        var e = g.Up(new(50, 20), 0.1);
        Assert.Equal(GestureKind.Swipe, e?.Kind);
        Assert.True(e!.Value.Direction.Y < -0.95f);
    }

    [Fact]
    public void SlowStrokeThatStopsIsADragNotASwipe()
    {
        var g = new GestureClassifier();
        g.Down(new(0, 0), 0);
        for (var i = 1; i <= 10; i++) g.Move(new(i * 6, 0), i * 0.1);
        g.Move(new(60, 0), 1.3);
        Assert.Equal(GestureKind.DragEnd, g.Up(new(60, 0), 1.4)?.Kind);
    }

    [Fact]
    public void BackAndForthIsARub()
    {
        var g = new GestureClassifier();
        g.Down(new(50, 50), 0);
        var t = 0.0;
        var rubs = 0;
        foreach (var x in new[] { 60, 75, 60, 40, 25, 40, 60, 75, 60, 40 })
        {
            t += 0.05;
            if (g.Move(new(x, 50), t)?.Kind == GestureKind.Rub) rubs++;
        }
        Assert.True(rubs > 0);
        var end = g.Up(new(40, 50), t + 0.05);
        Assert.Equal(GestureKind.RubEnd, end?.Kind);
        Assert.True(end!.Value.Count >= 2);
    }

    [Fact]
    public void MovingAfterALongPressIsAHeldDrag()
    {
        var g = new GestureClassifier();
        g.Down(new(0, 0), 0);
        Assert.Equal(GestureKind.LongPressStart, g.Tick(0.5)?.Kind);
        var d = g.Move(new(30, 0), 0.7);
        Assert.Equal(GestureKind.Drag, d?.Kind);
        Assert.True(d!.Value.Held);
        Assert.Equal(GestureKind.DragEnd, g.Up(new(200, 0), 0.72)?.Kind);
    }

    [Fact]
    public void JitterUnderTheSlopStillTaps()
    {
        var g = new GestureClassifier(pixelsPerDip: 1.5);
        g.Down(new(10, 10), 0);
        g.Move(new(13, 12), 0.03);
        Assert.Equal(GestureKind.Tap, g.Up(new(12, 9), 0.08)?.Kind);
    }

    [Fact]
    public void SlopIsSmallerInDipsOnHighDpi()
    {
        Assert.True(new GestureClassifier(1).Slop >= new GestureClassifier(2).Slop);
    }

    [Fact]
    public void CancelNeverCountsAsATap()
    {
        var g = new GestureClassifier();
        g.Down(new(0, 0), 0);
        Assert.Null(g.Cancel(0.05));
        Assert.False(g.IsDown);
    }

    [Theory]
    [InlineData(0, -0.8, BodyRegion.HeadTop)]
    [InlineData(0, -0.1, BodyRegion.Face)]
    [InlineData(-0.33, -0.17, BodyRegion.Face)]
    [InlineData(0.6, 0.22, BodyRegion.Cheeks)]
    [InlineData(-0.6, 0.22, BodyRegion.Cheeks)]
    [InlineData(0, 0.55, BodyRegion.Belly)]
    [InlineData(0, 0.85, BodyRegion.Feet)]
    [InlineData(-1.0, 0.4, BodyRegion.LeftHand)]
    [InlineData(1.0, 0.4, BodyRegion.RightHand)]
    [InlineData(0, -1.5, BodyRegion.None)]
    [InlineData(1.6, 0, BodyRegion.None)]
    public void NormalizedZones(double x, double y, BodyRegion expected) =>
        Assert.Equal(expected, BodyMap.FromNormalized(new Vector2((float)x, (float)y)));

    [Fact]
    public void AnchorsMapBackToTheirRegion()
    {
        foreach (var r in Enum.GetValues<BodyRegion>().Where(r => r != BodyRegion.None))
            Assert.Equal(r, BodyMap.FromNormalized(BodyMap.AnchorFor(r)));
    }

    private static readonly BodyJoints Joints = new(Top: new(100, 40), Bottom: new(100, 200), Center: new(100, 120), HalfWidth: 60,
        LeftHand: new(30, 160), RightHand: new(170, 160), HandRadius: 18);

    [Fact]
    public void ProjectedJointsPickRegions()
    {
        Assert.Equal(BodyRegion.HeadTop, BodyMap.FromJoints(new(100, 50), Joints));
        Assert.Equal(BodyRegion.Belly, BodyMap.FromJoints(new(100, 165), Joints));
        Assert.Equal(BodyRegion.LeftHand, BodyMap.FromJoints(new(35, 150), Joints));
        Assert.Equal(BodyRegion.RightHand, BodyMap.FromJoints(new(165, 170), Joints));
        Assert.Equal(BodyRegion.None, BodyMap.FromJoints(new(400, 120), Joints));
    }

    [Fact]
    public void RaisedHandIsFoundWhereItIsNotWhereTheZoneSays()
    {
        var raised = Joints with { RightHand = new(175, 50) };
        Assert.Equal(BodyRegion.RightHand, BodyMap.FromJoints(new(172, 55), raised));
    }

    [Fact]
    public void DenormalizeRoundTrips()
    {
        var p = BodyMap.Denormalize(BodyMap.AnchorFor(BodyRegion.Belly), Joints);
        Assert.Equal(BodyRegion.Belly, BodyMap.FromJoints(p, Joints));
    }

    [Fact]
    public void TapEscalationClimbsThenCoolsDown()
    {
        var e = new TapEscalation();
        Assert.Equal(TapMood.Single, e.For(1, 0));
        Assert.Equal(TapMood.Giggle, e.For(2, 0.3));
        Assert.Equal(TapMood.Ticklish, e.For(3, 0.6));
        Assert.Equal(TapMood.Ticklish, e.For(4, 0.9));
        Assert.Equal(TapMood.Dizzy, e.For(5, 1.2));
        Assert.Equal(TapMood.Settling, e.For(6, 1.5));
        Assert.Equal(TapMood.Settling, e.For(1, 4.0));
        Assert.Equal(TapMood.Single, e.For(1, 4.3));
    }
}
