using System.Numerics;

namespace DeskPal.Core;

// Pip's moves as functions of time. Each move describes the body in a few readable numbers (how far each arm is
// raised, where each foot stands, how the spine bends) and PipSolver turns that into joint rotations, placing the
// feet with a two-bone leg solve so planted feet stay planted while the hips move. The renderer bakes these into
// clips at 30 frames a second; the names match Bloop's clips so every nudge, stretch and reaction finds its move.
public static class PipMoves
{
    public readonly record struct Move(string Name, float Duration, bool Loop);

    public static readonly Move[] All =
    [
        new("idle_breathe", 3f, true),
        new("blink", 0.25f, false),
        new("poke_squish", 0.8f, false),
        new("wave", 1.5f, false),
        new("stretch_overhead", 4f, false),
        new("side_bend", 4f, false),
        new("shoulder_shrug", 2.5f, false),
        new("neck_roll", 4f, false),
        new("seated_twist", 4f, false),
        new("wrist_stretch", 4f, false),
        new("hip_flexor", 4f, false),
        new("calf_raises", 3f, false),
        new("walk", 1.4f, true),
        new("star_jump", 1.25f, false),
        new("high_five", 2f, false),
        new("celebrate", 2f, false),
    ];

    private const float S = PipRig.Scale;
    private const int Blink = 0, EyesHappy = 1, EyesSqueeze = 2, Smile = 3, MouthO = 4, Grin = 5;

    [ThreadStatic] private static PipSpec? _spec;

    public static void Sample(string move, float t, PipFrame frame)
    {
        var s = _spec ??= new PipSpec();
        s.Reset();
        switch (move)
        {
            case "idle_breathe": Idle(s, t); break;
            case "blink": s.Morph[Blink] = t < 0.08f ? t / 0.08f : Math.Max(0, 1 - (t - 0.08f) / 0.17f); break;
            case "poke_squish": Poke(s, t); break;
            case "wave": Wave(s, t); break;
            case "stretch_overhead": Overhead(s, t); break;
            case "side_bend": SideBend(s, t); break;
            case "shoulder_shrug": Shrug(s, t); break;
            case "neck_roll": NeckRoll(s, t); break;
            case "seated_twist": Twist(s, t); break;
            case "wrist_stretch": Wrists(s, t); break;
            case "hip_flexor": Lunge(s, t); break;
            case "calf_raises": CalfRaises(s, t); break;
            case "walk": Moonwalk(s, t); break;
            case "star_jump": StarJump(s, t); break;
            case "high_five": HighFive(s, t); break;
            case "celebrate": Celebrate(s, t); break;
        }
        PipSolver.Solve(s, frame);
    }

    private static void Idle(PipSpec s, float t)
    {
        var b = MathF.Sin(MathF.Tau * t / 3f);
        s.ChestPitch = -0.025f * b;
        s.HeadPitch = 0.02f * b;
        s.L.Raise = s.R.Raise = 0.17f + 0.02f * b;
        s.L.Shrug = s.R.Shrug = 0.0004f * S * b;
        s.HipsMove.Y = 0.0002f * S * b;
        s.HipsMove.X = 0.0004f * S * MathF.Sin(MathF.Tau * t / 3f * 0.5f + 0.4f) * MathF.Sin(MathF.Tau * t / 3f);
    }

    private static void Poke(PipSpec s, float t)
    {
        var k = Env(t, 0, 0.08f, 0.15f, 0.8f);
        s.HipsMove.Y = -0.003f * S * k;
        s.SpinePitch = -0.2f * k;
        s.HeadPitch = -0.1f * k;
        s.L.Raise = s.R.Raise = 0.17f + 0.9f * k;
        s.L.Elbow = s.R.Elbow = 0.15f + 0.6f * k;
        s.Morph[EyesSqueeze] = k;
        s.Morph[MouthO] = 0.8f * k;
    }

    private static void Wave(PipSpec s, float t)
    {
        var e = Env(t, 0, 0.3f, 1.2f, 1.5f);
        s.L.Raise = 0.17f + 2.2f * e;
        s.L.Forward = 0.25f * e;
        s.L.Elbow = 0.15f + 0.5f * e;
        s.L.ElbowSide = e * 0.45f * MathF.Sin(MathF.Tau * 2.4f * Math.Max(0, t - 0.3f));
        s.SpineRoll = -0.06f * e;
        s.HeadRoll = 0.1f * e;
        s.Morph[Smile] = 0.8f * e;
        s.Morph[EyesHappy] = 0.6f * e;
    }

    // Reach up on tiptoe: a small dip first, the arms sweep up with a little overshoot, a held breath, then down.
    private static void Overhead(PipSpec s, float t)
    {
        var dip = Bump(t, 0, 0.6f);
        var up = Env(t, 0.45f, 1.2f, 3.0f, 3.9f);
        var toes = 0.45f * Env(t, 1.0f, 1.6f, 2.8f, 3.4f);
        var breath = 0.03f * MathF.Sin(MathF.Tau * 0.8f * t) * up;
        s.L.Raise = s.R.Raise = 0.17f + 2.75f * up + breath;
        s.L.Forward = s.R.Forward = 0.1f * up;
        s.L.ElbowSide = s.R.ElbowSide = -0.3f * up;
        s.L.Elbow = s.R.Elbow = 0.15f * (1 - up) + 0.05f;
        s.L.Shrug = s.R.Shrug = 0.002f * S * up;
        s.HipsMove.Y = -0.0025f * S * dip + PipSolver.ToeRise(toes);
        s.FL.Pitch = s.FR.Pitch = toes;
        s.ChestPitch = -0.12f * up;
        s.HeadPitch = -0.15f * up;
        s.Morph[EyesHappy] = 0.5f * up;
        s.Morph[MouthO] = 0.35f * Bump(t, 1.0f, 1.9f);
    }

    // Arms up, lean to the left and hold, through the middle to the right and hold, arms down.
    private static void SideBend(PipSpec s, float t)
    {
        var up = Env(t, 0, 0.5f, 3.4f, 4.0f);
        var b = Env(t, 0.5f, 1.2f, 1.6f, 2.0f) - Env(t, 2.0f, 2.7f, 3.0f, 3.4f);
        s.L.Raise = 0.17f + 2.6f * up - 0.4f * Math.Max(b, 0) + 0.3f * Math.Max(-b, 0);
        s.R.Raise = 0.17f + 2.6f * up + 0.3f * Math.Max(b, 0) - 0.4f * Math.Max(-b, 0);
        s.L.Elbow = s.R.Elbow = 0.2f;
        s.SpineRoll = 0.22f * b;
        s.ChestRoll = 0.25f * b;
        s.HeadRoll = 0.1f * b;
        s.HipsMove.X = -0.0025f * S * b;
        s.Morph[Smile] = 0.5f * up;
    }

    private static void Shrug(PipSpec s, float t)
    {
        var k = Springy(Bump(t, 0.1f, 1.1f)) + Springy(Bump(t, 1.2f, 2.2f));
        s.L.Shrug = s.R.Shrug = 0.0035f * S * k;
        s.L.Raise = s.R.Raise = 0.2f;
        s.L.Forward = s.R.Forward = 0.12f * k;
        s.HeadPitch = 0.06f * k;
        s.ChestPitch = -0.03f * k;
        s.Morph[Smile] = 0.4f;
    }

    // Chin down, roll the head slowly ear to ear and back, eyes softly closed.
    private static void NeckRoll(PipSpec s, float t)
    {
        var e = Env(t, 0, 0.5f, 3.4f, 4.0f);
        var a = MathF.Tau * Math.Clamp((t - 0.5f) / 2.9f, 0, 1);
        s.HeadRoll = 0.38f * e * MathF.Sin(a);
        s.HeadPitch = e * (0.26f - 0.1f * (1 - MathF.Cos(a)) * 0.5f);
        s.L.Shrug = s.R.Shrug = -0.001f * S * e;
        s.Morph[Blink] = 0.7f * e;
        s.Morph[Smile] = 0.3f * e;
    }

    // A twist through the middle, hands in front as if holding the back of a chair, to the left then the right.
    private static void Twist(PipSpec s, float t)
    {
        var hands = Env(t, 0, 0.4f, 3.6f, 4.0f);
        var w = Env(t, 0.3f, 1.0f, 1.6f, 2.0f) - Env(t, 2.0f, 2.7f, 3.3f, 3.8f);
        s.SpineYaw = 0.3f * w;
        s.ChestYaw = 0.45f * w;
        s.HeadYaw = 0.25f * w;
        s.HipsYaw = -0.05f * w;
        s.L.Raise = s.R.Raise = 0.17f + 0.15f * hands;
        s.L.Forward = s.R.Forward = 0.9f * hands;
        s.L.Elbow = s.R.Elbow = 0.15f + 1.2f * hands;
        s.Morph[Smile] = 0.3f * hands;
    }

    // One arm straight out, fingers pulled back by the other hand, then the other side; Pip turns a little each way
    // so the reach reads from the front.
    private static void Wrists(PipSpec s, float t)
    {
        var a = Env(t, 0.2f, 0.7f, 1.7f, 2.0f);
        var b = Env(t, 2.0f, 2.5f, 3.5f, 3.8f);
        s.RootYaw = 0.35f * (a - b);
        Reach(ref s.L, ref s.R, a);
        Reach(ref s.R, ref s.L, b);
        s.Morph[Smile] = 0.35f * (a + b);

        static void Reach(ref PipSpec.Arm straight, ref PipSpec.Arm helper, float k)
        {
            if (k <= 0) return;
            straight.Forward += 1.45f * k;
            straight.Wrist += 0.9f * k;
            straight.Elbow -= 0.1f * k;
            helper.Forward += 1.2f * k;
            helper.Elbow += 0.6f * k;
            helper.Raise -= 0.45f * k;
        }
    }

    // A lunge for the hip flexors: turn side-on, step the left foot forward, back heel up, sink, the arm on the back
    // leg's side reaching up; a gentle pulse in the hold; step back.
    private static void Lunge(PipSpec s, float t)
    {
        var e = Env(t, 0, 0.8f, 3.2f, 4.0f);
        var pulse = 0.001f * S * MathF.Sin(MathF.Tau * 0.7f * t) * e;
        s.RootYaw = 0.75f * e;
        s.FL.Z = 0.012f * S * e;
        s.FL.Lift = 0.0035f * S * (Bump(t, 0.05f, 0.75f) + Bump(t, 3.25f, 3.95f));
        s.FR.Z = -0.011f * S * e;
        s.FR.Pitch = 0.65f * e;
        s.HipsMove.Y = -0.0065f * S * e - pulse;
        s.HipsPitch = -0.08f * e;
        s.ChestPitch = -0.1f * e;
        s.R.Raise = 0.17f + 2.6f * e;
        s.L.Raise = 0.17f + 0.5f * e;
        s.L.Elbow = 0.15f + 1.5f * e;
        s.L.ElbowSide = -0.9f * e;
        s.Morph[Smile] = 0.4f * e;
    }

    // Up onto the toes and down, twice, arms out in front for balance.
    private static void CalfRaises(PipSpec s, float t)
    {
        var r = Env(t, 0.2f, 0.7f, 1.0f, 1.4f) + Env(t, 1.6f, 2.1f, 2.4f, 2.8f);
        var arms = Env(t, 0, 0.3f, 2.7f, 3.0f);
        var p = 0.6f * r;
        s.FL.Pitch = s.FR.Pitch = p;
        s.HipsMove.Y = PipSolver.ToeRise(p);
        s.L.Forward = s.R.Forward = 0.3f * arms + 1.0f * r;
        s.L.Elbow = s.R.Elbow = 0.2f;
        s.Morph[Smile] = 0.5f * arms;
        s.Morph[EyesHappy] = 0.4f * r;
    }

    // The walk reminder: a moonwalk in place. Pip turns three-quarters, leans in, and his feet take turns: one slides
    // back flat on the floor while the other rides on its toes, then they swap. His head turns back to the viewer.
    private static void Moonwalk(PipSpec s, float t)
    {
        var u = (t / 1.4f) % 1f;
        s.RootYaw = -0.95f;
        Foot(ref s.FL, u);
        Foot(ref s.FR, (u + 0.5f) % 1f);
        var beat = MathF.Abs(MathF.Cos(MathF.Tau * u));
        s.HipsMove.Y = -0.0012f * S - 0.0008f * S * beat;
        s.HipsMove.X = 0.0008f * S * MathF.Sin(MathF.Tau * u);
        s.HipsYaw = 0.06f * MathF.Sin(MathF.Tau * u);
        s.SpinePitch = 0.08f;
        s.L.Raise = s.R.Raise = 0.22f;
        s.L.Forward = 0.25f * MathF.Sin(MathF.Tau * u);
        s.R.Forward = -0.25f * MathF.Sin(MathF.Tau * u);
        s.L.Elbow = s.R.Elbow = 0.6f;
        s.HeadYaw = 0.6f;
        s.HeadPitch = 0.05f * MathF.Sin(2 * MathF.Tau * u);
        s.Morph[Smile] = 0.6f;

        static void Foot(ref PipSpec.Leg leg, float u)
        {
            const float front = 0.006f * S, back = -0.008f * S;
            if (u < 0.5f)
            {
                leg.Z = front + (back - front) * Smooth(u / 0.5f);
                return;
            }
            var k = Smooth((u - 0.5f) / 0.5f);
            leg.Z = back + (front - back) * k;
            leg.Pitch = 0.62f * Math.Min(Ramp(u, 0.5f, 0.56f), 1 - Ramp(u, 0.94f, 1.0f));
            leg.Lift = 0.0012f * S * MathF.Sin(MathF.PI * k);
        }
    }

    private static void StarJump(PipSpec s, float t)
    {
        var air = Bump(t, 0.25f, 0.85f);
        var spread = Env(t, 0.3f, 0.5f, 0.75f, 0.95f);
        s.Root.Y = 0.012f * S * air;
        s.HipsMove.Y = -0.004f * S * (Bump(t, 0, 0.3f) + Bump(t, 0.85f, 1.15f));
        s.FL.X += 0.008f * S * spread;
        s.FR.X -= 0.008f * S * spread;
        s.FL.Lift = s.FR.Lift = 0.002f * S * air;
        s.L.Raise = s.R.Raise = 0.17f + 2.6f * spread;
        s.Morph[Grin] = 0.8f * spread;
        s.Morph[EyesHappy] = 0.7f * spread;
    }

    private static void HighFive(PipSpec s, float t)
    {
        var e = Env(t, 0.1f, 0.5f, 1.4f, 1.9f);
        var push = Bump(t, 0.75f, 1.0f);
        s.L.Raise = 0.17f + 1.7f * e;
        s.L.Forward = 0.9f * e + 0.25f * push;
        s.L.Elbow = 0.15f + 0.2f * e;
        s.L.Wrist = 0.6f * e;
        s.SpineRoll = -0.05f * e;
        s.HipsMove.Y = -0.002f * S * Bump(t, 0.65f, 0.95f);
        s.Root.Y = 0.003f * S * Bump(t, 0.9f, 1.2f);
        s.Morph[Grin] = 0.9f * e;
        s.Morph[EyesHappy] = 0.8f * e;
    }

    // Two hops with a full spin, arms pumping in a V.
    private static void Celebrate(PipSpec s, float t)
    {
        var e = Env(t, 0, 0.25f, 1.6f, 2.0f);
        s.Root.Y = 0.010f * S * Bump(t, 0.15f, 0.6f) + 0.008f * S * Bump(t, 0.75f, 1.2f);
        s.RootYaw = MathF.Tau * Ramp(t, 0.2f, 1.2f);
        s.HipsMove.Y = -0.003f * S * (Bump(t, 0, 0.2f) + Bump(t, 0.55f, 0.8f) + Bump(t, 1.15f, 1.45f));
        s.FL.Lift = s.FR.Lift = 0.0015f * S * (Bump(t, 0.15f, 0.6f) + Bump(t, 0.75f, 1.2f));
        s.L.Raise = s.R.Raise = 0.17f + 2.3f * e + 0.25f * MathF.Sin(MathF.Tau * 3 * t) * e;
        s.L.Elbow = s.R.Elbow = 0.2f;
        s.Morph[Grin] = e;
        s.Morph[EyesHappy] = e;
    }

    public static float Smooth(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    public static float Ramp(float t, float a, float b) => Smooth((t - a) / (b - a));

    // A quick rise that runs a little past 1 and settles back: the springy feel of every move.
    public static float Springy(float x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        const float c1 = 1.70158f, c3 = c1 + 1;
        var y = x - 1;
        return 1 + c3 * y * y * y + c1 * y * y;
    }

    // In over a..b with a springy overshoot, held, out over c..d.
    public static float Env(float t, float a, float b, float c, float d) =>
        Springy((t - a) / (b - a)) * (1 - Smooth((t - c) / (d - c)));

    public static float Bump(float t, float a, float b) => MathF.Sin(MathF.PI * Math.Clamp((t - a) / (b - a), 0, 1));
}

// The readable description of one moment of a move. Angles in radians; distances in metres.
public sealed class PipSpec
{
    public struct Arm
    {
        public float Raise, Forward, Twist, Elbow, ElbowSide, Wrist, Shrug;
    }

    public struct Leg
    {
        public float X, Z, Lift, Pitch, Yaw;
    }

    public Vector3 Root;
    public float RootYaw;
    public Vector3 HipsMove;
    public float HipsYaw, HipsRoll, HipsPitch;
    public float SpinePitch, SpineRoll, SpineYaw;
    public float ChestPitch, ChestRoll, ChestYaw;
    public float HeadPitch, HeadRoll, HeadYaw;
    public Arm L, R;
    public Leg FL, FR;
    public readonly float[] Morph = new float[PipRig.MorphNames.Length];

    public void Reset()
    {
        Root = HipsMove = Vector3.Zero;
        RootYaw = HipsYaw = HipsRoll = HipsPitch = 0;
        SpinePitch = SpineRoll = SpineYaw = ChestPitch = ChestRoll = ChestYaw = HeadPitch = HeadRoll = HeadYaw = 0;
        L = R = new Arm { Raise = 0.17f, Elbow = 0.15f };
        FL = new Leg { X = PipRig.HipSide };
        FR = new Leg { X = -PipRig.HipSide };
        Array.Clear(Morph);
    }
}

public static class PipSolver
{
    private const float L1 = PipRig.Thigh, L2 = PipRig.Shin;

    // How far the ankle (and so the hips) rises when the heel lifts by pitch radians with the toes kept down.
    public static float ToeRise(float pitch) => AnkleOffset(pitch).Y - PipRig.AnkleHeight;

    // Ankle position relative to the toe-kept-down rest spot when the foot pitches heel-up.
    private static Vector3 AnkleOffset(float pitch)
    {
        var c = PipRig.ToeContact;
        var (sin, cos) = MathF.SinCos(pitch);
        return new Vector3(0, -(c.Y * cos - c.Z * sin), c.Z - (c.Y * sin + c.Z * cos));
    }

    public static void Solve(PipSpec s, PipFrame f)
    {
        f.Rest();
        f.T[PipRig.Root] = s.Root;
        f.R[PipRig.Root] = Yaw(s.RootYaw);
        f.T[PipRig.Hips] += s.HipsMove;
        var hips = Rot(s.HipsYaw, s.HipsRoll, s.HipsPitch);
        f.R[PipRig.Hips] = hips;
        f.R[PipRig.Body] = Rot(s.SpineYaw, s.SpineRoll, s.SpinePitch);
        f.R[PipRig.Chest] = Rot(s.ChestYaw, s.ChestRoll, s.ChestPitch);
        f.R[PipRig.Neck] = Rot(s.HeadYaw, s.HeadRoll, s.HeadPitch);
        Arm(f, s.L, PipRig.ShoulderL, PipRig.ArmL, PipRig.ElbowL, PipRig.HandL, 1);
        Arm(f, s.R, PipRig.ShoulderR, PipRig.ArmR, PipRig.ElbowR, PipRig.HandR, -1);
        Leg(f, s.FL, hips, f.T[PipRig.Hips], PipRig.ThighL, PipRig.KneeL, PipRig.FootL);
        Leg(f, s.FR, hips, f.T[PipRig.Hips], PipRig.ThighR, PipRig.KneeR, PipRig.FootR);
        Array.Copy(s.Morph, f.Morph, f.Morph.Length);
    }

    private static Quaternion Yaw(float a) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, a);

    // Yaw (turn to his left), then roll (lean to his left), then pitch (lean forward), applied pitch first.
    private static Quaternion Rot(float yaw, float roll, float pitch) =>
        Yaw(yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -roll) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);

    // side is +1 for his left arm, -1 for his right, so the same numbers mirror.
    private static void Arm(PipFrame f, PipSpec.Arm a, int shoulder, int upper, int elbow, int hand, int side)
    {
        f.T[shoulder] += new Vector3(0, a.Shrug, 0);
        f.R[upper] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side * a.Raise)
                     * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -a.Forward)
                     * Quaternion.CreateFromAxisAngle(Vector3.UnitY, side * a.Twist);
        f.R[elbow] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side * a.ElbowSide)
                     * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -Math.Max(0, a.Elbow));
        f.R[hand] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, a.Wrist);
    }

    // Two-bone leg: aims the thigh at the ankle target, bends the knee forward (the way the foot points), keeps the
    // foot level with the floor apart from its own pitch. Everything is in the root's frame; the hips may be turned.
    private static void Leg(PipFrame f, PipSpec.Leg leg, Quaternion hips, Vector3 hipsAt, int thigh, int knee, int foot)
    {
        var footTurn = Yaw(leg.Yaw);
        var ankle = new Vector3(leg.X, PipRig.AnkleHeight + leg.Lift, leg.Z);
        var lift = AnkleOffset(leg.Pitch);
        ankle += Vector3.Transform(new Vector3(0, lift.Y - PipRig.AnkleHeight, lift.Z), footTurn);

        var hip = hipsAt + Vector3.Transform(PipRig.Joints[thigh].Offset, hips);
        var d = ankle - hip;
        var len = d.Length();
        var dir = len > 1e-6f ? d / len : -Vector3.UnitY;
        var dist = Math.Clamp(len, 0.35f * (L1 + L2), 0.999f * (L1 + L2));

        var aim = FromTo(-Vector3.UnitY, dir);
        var forward = Vector3.Transform(Vector3.UnitZ, footTurn);
        var axis = Vector3.Cross(dir, forward);
        axis = axis.LengthSquared() < 1e-8f ? Vector3.Transform(-Vector3.UnitX, footTurn) : Vector3.Normalize(axis);
        var alpha = MathF.Acos(Math.Clamp((L1 * L1 + dist * dist - L2 * L2) / (2 * L1 * dist), -1, 1));
        var gamma = MathF.Acos(Math.Clamp((L1 * L1 + L2 * L2 - dist * dist) / (2 * L1 * L2), -1, 1));
        var thighRoot = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, alpha) * aim);
        var axisLocal = Vector3.Transform(axis, Quaternion.Conjugate(thighRoot));
        var shinLocal = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axisLocal), -(MathF.PI - gamma));

        f.R[thigh] = Quaternion.Normalize(Quaternion.Conjugate(hips) * thighRoot);
        f.R[knee] = shinLocal;
        var shinRoot = thighRoot * shinLocal;
        var level = footTurn * Quaternion.CreateFromAxisAngle(Vector3.UnitX, leg.Pitch);
        f.R[foot] = Quaternion.Normalize(Quaternion.Conjugate(shinRoot) * level);
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var dot = Vector3.Dot(a, b);
        if (dot < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1 + dot));
    }
}
