namespace DeskPal.Core.Pose;

/// <summary>
/// Rig values for Bloop, already mirrored: "Left" is the user's own left, which is the left of the screen when Bloop
/// faces the user like a mirror. Lean and head tilt are positive toward the user's right (screen-right).
/// </summary>
/// <param name="HasPerson">False when nobody is in view; every other value is then neutral.</param>
/// <param name="LeftArmRaiseDeg">0 arm down, 90 out sideways, 180 straight up.</param>
/// <param name="LeanDeg">Body lean, -45..45.</param>
/// <param name="HeadTiltDeg">Head tilt, -45..45.</param>
/// <param name="Shrug">0 relaxed .. 1 full shrug.</param>
public readonly record struct MirrorValues(
    bool HasPerson, float LeftArmRaiseDeg, float RightArmRaiseDeg, float LeanDeg, float HeadTiltDeg, float Shrug)
{
    public static readonly MirrorValues Neutral = new(false, 0, 0, 0, 0, 0);
}

/// <summary>Turns frames into smoothed, clamped <see cref="MirrorValues"/>.</summary>
public sealed class MirrorPose
{
    private const float ResponseHz = 6f; // extra easing on top of the keypoint smoothing
    private readonly PoseSmoother _smoother;
    private readonly ShrugMeter _shrug = new();
    private bool _primed;
    private TimeSpan _lastTime;
    private float _lArm, _rArm, _lean, _head, _shrugAmt;

    public MirrorPose(float aspect = 1f) => _smoother = new PoseSmoother(aspect);

    public MirrorValues Current { get; private set; } = MirrorValues.Neutral;

    public MirrorValues Update(PoseFrame frame)
    {
        var pose = _smoother.Update(frame);
        var m = PoseMetrics.Compute(pose);
        if (!m.HasPerson)
        {
            _primed = false;
            _shrug.Reset();
            return Current = MirrorValues.Neutral;
        }

        float dt = _primed ? Math.Clamp((float)(frame.Time - _lastTime).TotalSeconds, 0f, 0.5f) : 0f;
        _lastTime = frame.Time;
        float a = _primed ? 1f - MathF.Exp(-ResponseHz * dt) : 1f;
        _primed = true;

        if (PoseMetrics.ArmAngle(pose, BodyPoint.LeftShoulder, BodyPoint.LeftElbow, BodyPoint.LeftWrist, out var la))
            _lArm += a * (Math.Clamp(la, 0f, 180f) - _lArm);
        if (PoseMetrics.ArmAngle(pose, BodyPoint.RightShoulder, BodyPoint.RightElbow, BodyPoint.RightWrist, out var ra))
            _rArm += a * (Math.Clamp(ra, 0f, 180f) - _rArm);
        _lean += a * (Math.Clamp(m.LeanDeg, -45f, 45f) - _lean);
        if (m.HasHeadTilt) _head += a * (Math.Clamp(m.HeadTiltDeg, -45f, 45f) - _head);
        _shrugAmt += a * (_shrug.Update(m, dt) - _shrugAmt);

        return Current = new MirrorValues(true, _lArm, _rArm, _lean, _head, Math.Clamp(_shrugAmt, 0f, 1f));
    }
}
