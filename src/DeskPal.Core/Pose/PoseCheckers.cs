namespace DeskPal.Core.Pose;

/// <summary>One frame's verdict from a checker. Side is 0 (user's left) or 1 (user's right) for two-sided stretches.</summary>
internal readonly record struct PoseVerdict(bool InPose, int Side, string? Reason)
{
    public static PoseVerdict Pass(int side = 0) => new(true, side, null);
    public static PoseVerdict Fail(string reason) => new(false, 0, reason);
}

/// <summary>Judges one stretch from body-relative measures. Checkers may keep a little state (a neutral baseline).</summary>
internal abstract class PoseChecker
{
    /// <summary>1 for a single pose, 2 when both sides must be done.</summary>
    public abstract int Sides { get; }

    public abstract PoseVerdict Evaluate(SmoothedPose p, in PoseMetrics m, float dt);

    /// <summary>Stretches a seated webcam can see. wrist-stretch (no finger or palm points in the 17-point model),
    /// hip-flexor (standing lunge, hips and legs out of frame) and calf-raises (ankles out of frame) cannot be judged.</summary>
    public static PoseChecker? For(string stretchId) => stretchId switch
    {
        "overhead-reach" => new OverheadReachChecker(),
        "side-bend" => new SideBendChecker(),
        "shoulder-shrug" => new ShoulderShrugChecker(),
        "neck-roll" => new NeckRollChecker(),
        "seated-twist" => new SeatedTwistChecker(),
        _ => null,
    };
}

/// <summary>Both hands clearly above the head.</summary>
internal sealed class OverheadReachChecker : PoseChecker
{
    public const string Reason = "Pose_RaiseArms";
    private const float WristAboveHeadU = 0.2f;

    public override int Sides => 1;

    public override PoseVerdict Evaluate(SmoothedPose p, in PoseMetrics m, float dt)
    {
        float head = m.HasNose ? m.NoseY : m.Cy - 0.8f * m.U;
        bool left = ArmUp(p, BodyPoint.LeftElbow, BodyPoint.LeftWrist, head, m.U);
        bool right = ArmUp(p, BodyPoint.RightElbow, BodyPoint.RightWrist, head, m.U);
        return left && right ? PoseVerdict.Pass() : PoseVerdict.Fail(Reason);
    }

    // A raised wrist often leaves the top of the image, so a visible elbow above the head also counts.
    private static bool ArmUp(SmoothedPose p, BodyPoint elbow, BodyPoint wrist, float headY, float u)
    {
        if (p.Has(wrist)) return p.Y[(int)wrist] < headY - WristAboveHeadU * u;
        if (p.Has(elbow)) return p.Y[(int)elbow] < headY;
        return false;
    }
}

/// <summary>Lean the upper body sideways, once to each side.</summary>
internal sealed class SideBendChecker : PoseChecker
{
    public const string Reason = "Pose_LeanSideways";
    public const float LeanDegrees = 12f;

    public override int Sides => 2;

    public override PoseVerdict Evaluate(SmoothedPose p, in PoseMetrics m, float dt) =>
        MathF.Abs(m.LeanDeg) >= LeanDegrees ? PoseVerdict.Pass(m.LeanDeg > 0 ? 1 : 0) : PoseVerdict.Fail(Reason);
}

/// <summary>Shoulders pulled up toward the ears (relative to the user's own neutral).</summary>
internal sealed class ShoulderShrugChecker : PoseChecker
{
    public const string Reason = "Pose_ShrugHigher";
    private const float RatioLimit = 0.82f;
    private readonly ShrugMeter _meter = new();

    public override int Sides => 1;

    public override PoseVerdict Evaluate(SmoothedPose p, in PoseMetrics m, float dt)
    {
        if (!m.HasNose) return PoseVerdict.Fail("Pose_FaceCamera");
        _meter.Update(m, dt);
        return _meter.Ready && _meter.Ratio <= RatioLimit ? PoseVerdict.Pass() : PoseVerdict.Fail(Reason);
    }
}

/// <summary>Tilt the head toward each shoulder (the side tilt of a neck roll; the circle itself cannot be seen).</summary>
internal sealed class NeckRollChecker : PoseChecker
{
    public const string Reason = "Pose_TiltHead";
    public const float TiltDegrees = 15f;

    public override int Sides => 2;

    public override PoseVerdict Evaluate(SmoothedPose p, in PoseMetrics m, float dt)
    {
        if (!m.HasHeadTilt) return PoseVerdict.Fail("Pose_FaceCamera");
        return MathF.Abs(m.RelHeadTiltDeg) >= TiltDegrees
            ? PoseVerdict.Pass(m.RelHeadTiltDeg > 0 ? 1 : 0)
            : PoseVerdict.Fail(Reason);
    }
}

/// <summary>Turn the torso: shoulder width in the image shrinks against the user's own square-on width, and the head follows the turn.</summary>
internal sealed class SeatedTwistChecker : PoseChecker
{
    public const string Reason = "Pose_TwistMore";
    private const float WidthRatioLimit = 0.8f;
    private const float NoseOffsetU = 0.1f;
    private const float Adapt = 0.02f;
    private float _baseline;

    public override int Sides => 2;

    public override PoseVerdict Evaluate(SmoothedPose p, in PoseMetrics m, float dt)
    {
        if (_baseline <= 0f) _baseline = m.Width;
        else _baseline = m.Width > _baseline ? m.Width : _baseline - (_baseline - m.Width) * Adapt * dt;
        if (!m.HasNose) return PoseVerdict.Fail("Pose_FaceCamera");
        if (m.Width / _baseline > WidthRatioLimit) return PoseVerdict.Fail(Reason);
        // Turning to the user's left moves the nose toward image-right (larger x).
        float offset = (m.NoseX - m.Cx) / m.U;
        if (MathF.Abs(offset) < NoseOffsetU) return PoseVerdict.Fail(Reason);
        return PoseVerdict.Pass(offset > 0 ? 0 : 1);
    }
}
