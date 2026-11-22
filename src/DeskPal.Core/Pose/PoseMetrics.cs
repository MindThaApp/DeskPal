namespace DeskPal.Core.Pose;

/// <summary>
/// Body-relative measures for one smoothed frame. Everything is expressed against shoulder width (U) or as angles,
/// so camera distance and position do not matter. Left and right are the user's own sides; angles are positive
/// toward the user's right (which is screen-right when Bloop mirrors the user).
/// </summary>
internal struct PoseMetrics
{
    public const float MinShoulderWidth = 0.04f;

    public bool HasPerson;
    public float U;           // shoulder distance
    public float Width;       // horizontal shoulder distance (shrinks when the torso turns)
    public float Cx, Cy;      // shoulder centre
    public float ShoulderTiltDeg;
    public float LeanDeg;     // from hips when visible, else the shoulder tilt
    public bool HasNose;
    public float NoseX, NoseY;
    public bool HasHeadTilt;
    public float HeadTiltDeg;    // absolute
    public float RelHeadTiltDeg; // against the shoulder line

    public static PoseMetrics Compute(SmoothedPose p)
    {
        var m = new PoseMetrics();
        if (!p.Has(BodyPoint.LeftShoulder) || !p.Has(BodyPoint.RightShoulder)) return m;
        float lx = p.X[(int)BodyPoint.LeftShoulder], ly = p.Y[(int)BodyPoint.LeftShoulder];
        float rx = p.X[(int)BodyPoint.RightShoulder], ry = p.Y[(int)BodyPoint.RightShoulder];
        float u = MathF.Sqrt((lx - rx) * (lx - rx) + (ly - ry) * (ly - ry));
        if (u < MinShoulderWidth) return m;

        m.HasPerson = true;
        m.U = u;
        m.Width = MathF.Abs(lx - rx);
        m.Cx = (lx + rx) * 0.5f;
        m.Cy = (ly + ry) * 0.5f;
        // The user's right shoulder sits at smaller x in the un-mirrored image.
        m.ShoulderTiltDeg = Deg(MathF.Atan2(ry - ly, MathF.Max(lx - rx, 0.2f * u)));
        m.LeanDeg = m.ShoulderTiltDeg;

        if (p.Has(BodyPoint.LeftHip) && p.Has(BodyPoint.RightHip))
        {
            float hx = (p.X[(int)BodyPoint.LeftHip] + p.X[(int)BodyPoint.RightHip]) * 0.5f;
            float hy = (p.Y[(int)BodyPoint.LeftHip] + p.Y[(int)BodyPoint.RightHip]) * 0.5f;
            if (hy - m.Cy > 0.5f * u)
                m.LeanDeg = Deg(MathF.Atan2(hx - m.Cx, hy - m.Cy));
        }

        if (p.Has(BodyPoint.Nose))
        {
            m.HasNose = true;
            m.NoseX = p.X[(int)BodyPoint.Nose];
            m.NoseY = p.Y[(int)BodyPoint.Nose];
        }

        if (Tilt(p, BodyPoint.LeftEar, BodyPoint.RightEar, u, out var head)
            || Tilt(p, BodyPoint.LeftEye, BodyPoint.RightEye, u, out head))
        {
            m.HasHeadTilt = true;
            m.HeadTiltDeg = head;
            m.RelHeadTiltDeg = head - m.ShoulderTiltDeg;
        }
        return m;
    }

    private static bool Tilt(SmoothedPose p, BodyPoint left, BodyPoint right, float u, out float deg)
    {
        deg = 0;
        if (!p.Has(left) || !p.Has(right)) return false;
        float dx = p.X[(int)left] - p.X[(int)right];
        float dy = p.Y[(int)right] - p.Y[(int)left];
        deg = Deg(MathF.Atan2(dy, MathF.Max(dx, 0.1f * u)));
        return true;
    }

    /// <summary>Arm angle from straight down: 0 down, 90 horizontal, 180 straight up. Uses the wrist, else the elbow.</summary>
    public static bool ArmAngle(SmoothedPose p, BodyPoint shoulder, BodyPoint elbow, BodyPoint wrist, out float deg)
    {
        deg = 0;
        if (!p.Has(shoulder)) return false;
        BodyPoint tip;
        if (p.Has(wrist)) tip = wrist;
        else if (p.Has(elbow)) tip = elbow;
        else return false;
        float dx = MathF.Abs(p.X[(int)tip] - p.X[(int)shoulder]);
        float dy = p.Y[(int)tip] - p.Y[(int)shoulder];
        deg = Deg(MathF.Atan2(dx, dy));
        return true;
    }

    private static float Deg(float rad) => rad * (180f / MathF.PI);
}

/// <summary>Tracks how high the shoulders sit under the nose against a slowly adapting neutral, so a shrug reads the same at any distance.</summary>
internal sealed class ShrugMeter
{
    private const float FullShrugDrop = 0.2f;   // 20% closer to the head = a full shrug
    private const float Adapt = 0.01f;          // baseline drifts toward the present at 1%/s
    private const float Warmup = 0.5f;          // seconds of data before the baseline is trusted

    private float _baseline;
    private float _age;

    public bool Ready => _age >= Warmup;
    public float Ratio { get; private set; } = 1f;

    public void Reset() { _baseline = 0; _age = 0; Ratio = 1f; }

    /// <summary>Returns the shrug amount 0..1 (0 when unknown).</summary>
    public float Update(in PoseMetrics m, float dt)
    {
        if (!m.HasPerson || !m.HasNose) return 0f;
        float r = (m.Cy - m.NoseY) / m.U;
        if (r <= 0.05f) return 0f;
        if (_baseline <= 0f) _baseline = r;
        else
        {
            _baseline = r > _baseline ? r : _baseline - (_baseline - r) * Adapt * dt;
            _age += dt;
        }
        Ratio = r / _baseline;
        return Math.Clamp((1f - Ratio) / FullShrugDrop, 0f, 1f);
    }
}
