using DeskPal.Core.Pose;
using Xunit;

namespace DeskPal.Core.Tests;

/// <summary>Synthetic seated body, in the un-mirrored camera image: the user's left side is at larger x.</summary>
internal sealed class PoseBody
{
    public const double Fps = 15;
    public readonly Keypoint[] P = new Keypoint[PoseFrame.PointCount];

    private PoseBody() { }

    public static PoseBody Neutral()
    {
        var b = new PoseBody();
        b.Set(BodyPoint.Nose, .5f, .45f);
        b.Set(BodyPoint.LeftEye, .54f, .42f); b.Set(BodyPoint.RightEye, .46f, .42f);
        b.Set(BodyPoint.LeftEar, .57f, .43f); b.Set(BodyPoint.RightEar, .43f, .43f);
        b.Set(BodyPoint.LeftShoulder, .6f, .6f); b.Set(BodyPoint.RightShoulder, .4f, .6f);
        b.Set(BodyPoint.LeftElbow, .62f, .78f); b.Set(BodyPoint.RightElbow, .38f, .78f);
        b.Set(BodyPoint.LeftWrist, .62f, .92f); b.Set(BodyPoint.RightWrist, .38f, .92f);
        b.Set(BodyPoint.LeftHip, .57f, .95f); b.Set(BodyPoint.RightHip, .43f, .95f);
        return b;
    }

    public PoseBody Clone() { var b = new PoseBody(); P.CopyTo(b.P, 0); return b; }

    public PoseBody Set(BodyPoint p, float x, float y) { P[(int)p] = new Keypoint(x, y, 0.9f); return this; }
    public PoseBody Drop(params BodyPoint[] pts) { foreach (var p in pts) P[(int)p] = new Keypoint(0, 0, 0.05f); return this; }

    private static readonly BodyPoint[] Upper =
    [
        BodyPoint.Nose, BodyPoint.LeftEye, BodyPoint.RightEye, BodyPoint.LeftEar, BodyPoint.RightEar,
        BodyPoint.LeftShoulder, BodyPoint.RightShoulder, BodyPoint.LeftElbow, BodyPoint.RightElbow,
        BodyPoint.LeftWrist, BodyPoint.RightWrist,
    ];
    private static readonly BodyPoint[] Head =
        [BodyPoint.Nose, BodyPoint.LeftEye, BodyPoint.RightEye, BodyPoint.LeftEar, BodyPoint.RightEar];

    /// <summary>Positive degrees lean the upper body toward the user's right (smaller x).</summary>
    public PoseBody Lean(float deg) => Rotate(Upper, .5f, .95f, deg);
    public PoseBody HeadTilt(float deg) => Rotate(Head, .5f, .6f, deg);

    private PoseBody Rotate(BodyPoint[] pts, float cx, float cy, float deg)
    {
        double t = deg * Math.PI / 180, s = Math.Sin(t), c = Math.Cos(t);
        foreach (var bp in pts)
        {
            var k = P[(int)bp];
            double dx = k.X - cx, dy = k.Y - cy;
            P[(int)bp] = new Keypoint((float)(cx + dx * c + dy * s), (float)(cy - dx * s + dy * c), k.Score);
        }
        return this;
    }

    public PoseBody RaiseBothHands()
    {
        Set(BodyPoint.LeftElbow, .6f, .38f); Set(BodyPoint.RightElbow, .4f, .38f);
        Set(BodyPoint.LeftWrist, .6f, .18f); Set(BodyPoint.RightWrist, .4f, .18f);
        return this;
    }

    public PoseBody Shrug()
    {
        foreach (var bp in new[] { BodyPoint.LeftShoulder, BodyPoint.RightShoulder, BodyPoint.LeftElbow, BodyPoint.RightElbow, BodyPoint.LeftWrist, BodyPoint.RightWrist })
        {
            var k = P[(int)bp]; P[(int)bp] = k with { Y = k.Y - .05f };
        }
        return this;
    }

    /// <summary>Torso turned: narrower shoulders, nose pushed sideways (toward the user's left when positive).</summary>
    public PoseBody Twist(float noseShift)
    {
        Set(BodyPoint.LeftShoulder, .57f, .6f); Set(BodyPoint.RightShoulder, .43f, .6f);
        Set(BodyPoint.Nose, .5f + noseShift, .45f);
        return this;
    }

    public PoseBody NoPerson() { for (int i = 0; i < P.Length; i++) P[i] = new Keypoint(0, 0, 0f); return this; }

    /// <summary>Camera framing: scale about the image centre plus an offset.</summary>
    public PoseFrame Frame(double seconds, float scale = 1f, float ox = 0f, float oy = 0f)
    {
        var pts = new Keypoint[PoseFrame.PointCount];
        for (int i = 0; i < pts.Length; i++)
        {
            var k = P[i];
            pts[i] = new Keypoint((k.X - .5f) * scale + .5f + ox, (k.Y - .5f) * scale + .5f + oy, k.Score);
        }
        return new PoseFrame(TimeSpan.FromSeconds(seconds), pts);
    }
}

internal sealed class PoseRunner
{
    public double T;
    public float Scale = 1f, Ox, Oy;
    public void Run(StretchCheck c, PoseBody b, double seconds)
    {
        int n = (int)Math.Round(seconds * PoseBody.Fps);
        for (int i = 0; i < n; i++) { c.Update(b.Frame(T, Scale, Ox, Oy)); T += 1 / PoseBody.Fps; }
    }
}

public class PoseStretchCheckTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(6);

    private static StretchCheck Make(string id) => new(id, 30, Hold);

    [Fact]
    public void CheckableSetMatchesWhatASeatedWebcamCanSee()
    {
        foreach (var id in new[] { "overhead-reach", "side-bend", "shoulder-shrug", "neck-roll", "seated-twist" })
            Assert.True(StretchCheck.IsCheckable(id), id);
        foreach (var id in new[] { "wrist-stretch", "hip-flexor", "calf-raises", "nope" })
        {
            Assert.False(StretchCheck.IsCheckable(id), id);
            Assert.Null(StretchCheck.Create(id, 30));
        }
    }

    [Fact]
    public void DefaultHoldIsAFractionOfTheStretchBetween5And15Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), StretchCheck.DefaultHold(30));
        Assert.Equal(TimeSpan.FromSeconds(5), StretchCheck.DefaultHold(5));
        Assert.Equal(TimeSpan.FromSeconds(15), StretchCheck.DefaultHold(60));
    }

    [Fact]
    public void OverheadReach_PassesCorrectPoseAndFailsNeutral()
    {
        var neutral = Make("overhead-reach");
        var r = new PoseRunner();
        r.Run(neutral, PoseBody.Neutral(), 4);
        Assert.Equal(StretchState.GetReady, neutral.State);
        Assert.Equal(0f, neutral.Progress);
        Assert.Equal("Pose_RaiseArms", neutral.Reason);

        var c = Make("overhead-reach");
        r = new PoseRunner();
        r.Run(c, PoseBody.Neutral().RaiseBothHands(), 7);
        Assert.Equal(StretchState.Done, c.State);
        Assert.Equal(1f, c.Progress);
    }

    [Fact]
    public void OneHandUpIsNotEnough()
    {
        var b = PoseBody.Neutral().Set(BodyPoint.LeftWrist, .6f, .18f).Set(BodyPoint.LeftElbow, .6f, .38f);
        var c = Make("overhead-reach");
        new PoseRunner().Run(c, b, 5);
        Assert.Equal(StretchState.GetReady, c.State);
    }

    [Fact]
    public void OverheadReach_ElbowsAboveHeadCountWhenWristsLeaveTheImage()
    {
        var b = PoseBody.Neutral().RaiseBothHands().Set(BodyPoint.LeftElbow, .6f, .3f).Set(BodyPoint.RightElbow, .4f, .3f)
            .Drop(BodyPoint.LeftWrist, BodyPoint.RightWrist);
        var c = Make("overhead-reach");
        new PoseRunner().Run(c, b, 7);
        Assert.Equal(StretchState.Done, c.State);

        var none = PoseBody.Neutral().RaiseBothHands().Drop(BodyPoint.LeftWrist, BodyPoint.RightWrist, BodyPoint.LeftElbow, BodyPoint.RightElbow);
        var c2 = Make("overhead-reach");
        new PoseRunner().Run(c2, none, 7);
        Assert.NotEqual(StretchState.Done, c2.State);
    }

    [Fact]
    public void NoShouldersMeansNoPerson()
    {
        var c = Make("overhead-reach");
        new PoseRunner().Run(c, PoseBody.Neutral().NoPerson(), 2);
        Assert.Equal(StretchState.NoPerson, c.State);
        Assert.Equal(StretchCheck.NoPersonReason, c.Reason);

        var half = PoseBody.Neutral().RaiseBothHands().Drop(BodyPoint.LeftShoulder);
        var c2 = Make("overhead-reach");
        new PoseRunner().Run(c2, half, 2);
        Assert.Equal(StretchState.NoPerson, c2.State);
    }

    [Theory]
    [InlineData("overhead-reach", 0.6f, 0f, 0f)]
    [InlineData("overhead-reach", 1.0f, 0.1f, -0.05f)]
    [InlineData("side-bend", 0.7f, -0.1f, 0.05f)]
    [InlineData("side-bend", 1.0f, 0f, 0f)]
    public void CheckersIgnoreDistanceAndPosition(string id, float scale, float ox, float oy)
    {
        var c = Make(id);
        var r = new PoseRunner { Scale = scale, Ox = ox, Oy = oy };
        var neutral = PoseBody.Neutral();
        r.Run(c, neutral, 1);
        Assert.Equal(StretchState.GetReady, c.State);
        if (id == "overhead-reach") r.Run(c, neutral.Clone().RaiseBothHands(), 7);
        else
        {
            r.Run(c, neutral.Clone().Lean(20), 4);
            r.Run(c, neutral.Clone().Lean(-20), 4);
        }
        Assert.Equal(StretchState.Done, c.State);
    }

    [Fact]
    public void ShrugIsRelativeToTheUsersOwnNeutral()
    {
        foreach (var scale in new[] { 0.6f, 1f, 1.3f })
        {
            var c = Make("shoulder-shrug");
            var r = new PoseRunner { Scale = scale };
            r.Run(c, PoseBody.Neutral(), 2);
            Assert.Equal(StretchState.GetReady, c.State);
            Assert.Equal("Pose_ShrugHigher", c.Reason);
            r.Run(c, PoseBody.Neutral().Shrug(), 7);
            Assert.Equal(StretchState.Done, c.State);
        }
    }

    [Fact]
    public void NeckRoll_NeedsHeadTiltOnBothSides()
    {
        var c = Make("neck-roll");
        var r = new PoseRunner();
        r.Run(c, PoseBody.Neutral(), 2);
        Assert.Equal(StretchState.GetReady, c.State);
        r.Run(c, PoseBody.Neutral().HeadTilt(25), 4);
        Assert.NotEqual(StretchState.Done, c.State);
        r.Run(c, PoseBody.Neutral().HeadTilt(-25), 4);
        Assert.Equal(StretchState.Done, c.State);
    }

    [Fact]
    public void NeckRoll_NoFaceMeansAskToFaceCamera()
    {
        var c = Make("neck-roll");
        new PoseRunner().Run(c, PoseBody.Neutral().Drop(BodyPoint.LeftEar, BodyPoint.RightEar, BodyPoint.LeftEye, BodyPoint.RightEye), 2);
        Assert.Equal("Pose_FaceCamera", c.Reason);
    }

    [Fact]
    public void SeatedTwist_NeedsATurnEachWay()
    {
        var c = Make("seated-twist");
        var r = new PoseRunner();
        r.Run(c, PoseBody.Neutral(), 2);
        Assert.Equal(StretchState.GetReady, c.State);
        r.Run(c, PoseBody.Neutral().Twist(.06f), 4);
        Assert.NotEqual(StretchState.Done, c.State);
        r.Run(c, PoseBody.Neutral().Twist(-.06f), 4);
        Assert.Equal(StretchState.Done, c.State);
    }

    [Fact]
    public void SideBend_BothSidesRequired_SameSideAgainAsksToSwitch()
    {
        var c = Make("side-bend");
        var r = new PoseRunner();
        r.Run(c, PoseBody.Neutral().Lean(20), 6);
        Assert.NotEqual(StretchState.Done, c.State);
        Assert.Equal(1f, c.SideProgress(1), 0.02f);
        Assert.Equal(0.5f, c.Progress, 0.03f);
        Assert.Equal(StretchState.GetReady, c.State);
        Assert.Equal(StretchCheck.SwitchSidesReason, c.Reason);

        r.Run(c, PoseBody.Neutral().Lean(-20), 4);
        Assert.Equal(StretchState.Done, c.State);
    }

    [Fact]
    public void ShortLossIsForgivenLongLossDrains()
    {
        var c = Make("overhead-reach");
        var r = new PoseRunner();
        var up = PoseBody.Neutral().RaiseBothHands();
        r.Run(c, up, 3);
        float before = c.Progress;
        Assert.Equal(StretchState.Holding, c.State);

        r.Run(c, PoseBody.Neutral(), 0.4); // inside the grace
        Assert.Equal(StretchState.Holding, c.State);
        Assert.Equal(before, c.Progress, 0.001f);
        Assert.Equal("Pose_RaiseArms", c.Reason);

        r.Run(c, up, 1);
        Assert.True(c.Progress > before);

        float peak = c.Progress;
        r.Run(c, PoseBody.Neutral(), 3); // beyond the grace
        Assert.Equal(StretchState.GetReady, c.State);
        Assert.True(c.Progress < peak);
        Assert.True(c.Progress > 0f);
    }

    [Fact]
    public void RaisedPoseTakesAMomentToSettleBeforeTheRingFills()
    {
        var c = Make("overhead-reach");
        var r = new PoseRunner();
        r.Run(c, PoseBody.Neutral().RaiseBothHands(), 0.2);
        Assert.Equal(StretchState.InPose, c.State);
        Assert.Equal(0f, c.Progress);
        Assert.Null(c.Reason);
    }

    [Fact]
    public void DoneIsSticky()
    {
        var c = Make("overhead-reach");
        var r = new PoseRunner();
        r.Run(c, PoseBody.Neutral().RaiseBothHands(), 7);
        Assert.Equal(StretchState.Done, c.State);
        r.Run(c, PoseBody.Neutral().NoPerson(), 3);
        Assert.Equal(StretchState.Done, c.State);
        Assert.Equal(1f, c.Progress);
        Assert.Null(c.Reason);
    }

    [Fact]
    public void LowConfidencePointsAreTreatedAsMissingAndDroppedFramesAreSurvived()
    {
        var c = Make("overhead-reach");
        var r = new PoseRunner();
        var up = PoseBody.Neutral().RaiseBothHands();
        r.Run(c, up, 2);
        r.T += 0.25; // camera hiccup: a quarter second with no frames at all
        r.Run(c, up, 5);
        Assert.Equal(StretchState.Done, c.State);
    }
}
