using DeskPal.Core.Pose;
using Xunit;

namespace DeskPal.Core.Tests;

public class PoseMirrorTests
{
    private static MirrorValues Run(PoseBody warmup, PoseBody body, double seconds = 2, float scale = 1f)
    {
        var m = new MirrorPose();
        double t = 0;
        for (int i = 0; i < 15; i++) { m.Update(warmup.Frame(t, scale)); t += 1 / PoseBody.Fps; }
        int n = (int)(seconds * PoseBody.Fps);
        for (int i = 0; i < n; i++) { m.Update(body.Frame(t, scale)); t += 1 / PoseBody.Fps; }
        return m.Current;
    }

    [Fact]
    public void NoPersonIsNeutral()
    {
        var v = Run(PoseBody.Neutral(), PoseBody.Neutral().NoPerson());
        Assert.False(v.HasPerson);
        Assert.Equal(MirrorValues.Neutral, v);
    }

    [Fact]
    public void RestingBodyIsNearNeutral()
    {
        var v = Run(PoseBody.Neutral(), PoseBody.Neutral());
        Assert.True(v.HasPerson);
        Assert.InRange(v.LeftArmRaiseDeg, 0, 10);
        Assert.InRange(v.RightArmRaiseDeg, 0, 10);
        Assert.InRange(v.LeanDeg, -2, 2);
        Assert.InRange(v.HeadTiltDeg, -2, 2);
        Assert.InRange(v.Shrug, 0, 0.05);
    }

    [Fact]
    public void UsersLeftArmDrivesBloopsScreenLeftArm()
    {
        var b = PoseBody.Neutral().Set(BodyPoint.LeftWrist, .6f, .2f).Set(BodyPoint.LeftElbow, .6f, .4f);
        var v = Run(PoseBody.Neutral(), b);
        Assert.InRange(v.LeftArmRaiseDeg, 165, 180);
        Assert.InRange(v.RightArmRaiseDeg, 0, 10);

        var side = PoseBody.Neutral().Set(BodyPoint.RightWrist, .15f, .6f).Set(BodyPoint.RightElbow, .28f, .6f);
        var v2 = Run(PoseBody.Neutral(), side);
        Assert.InRange(v2.RightArmRaiseDeg, 80, 100);
    }

    [Fact]
    public void LeaningToTheUsersRightIsPositive()
    {
        Assert.InRange(Run(PoseBody.Neutral(), PoseBody.Neutral().Lean(20)).LeanDeg, 15, 25);
        Assert.InRange(Run(PoseBody.Neutral(), PoseBody.Neutral().Lean(-20)).LeanDeg, -25, -15);
    }

    [Fact]
    public void HeadTiltAndShrugFollowTheUser()
    {
        Assert.InRange(Run(PoseBody.Neutral(), PoseBody.Neutral().HeadTilt(20)).HeadTiltDeg, 15, 25);
        Assert.InRange(Run(PoseBody.Neutral(), PoseBody.Neutral().HeadTilt(-20)).HeadTiltDeg, -25, -15);
        Assert.True(Run(PoseBody.Neutral(), PoseBody.Neutral().Shrug()).Shrug > 0.8f);
        Assert.True(Run(PoseBody.Neutral(), PoseBody.Neutral().Shrug(), scale: 0.6f).Shrug > 0.8f);
    }

    [Fact]
    public void ValuesAreClamped()
    {
        var v = Run(PoseBody.Neutral(), PoseBody.Neutral().Lean(80).HeadTilt(70));
        Assert.InRange(v.LeanDeg, -45, 45);
        Assert.InRange(v.HeadTiltDeg, -45, 45);
        Assert.InRange(v.Shrug, 0, 1);
    }
}
