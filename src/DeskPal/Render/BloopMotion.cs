using System.Numerics;
using DeskPal.Core;

namespace DeskPal.Render;

// Procedural reactions layered over Bloop's clips: springs (squash, wobble, spin, hop, a dent at the touched spot,
// arm follow), timed shakes and sways, and morph overlays (eyes, mouth, cheeks). Springs step at 1/240 s inside the
// viewport's loop; everything here is preallocated so a frame allocates nothing.
internal sealed class BloopMotion
{
    private struct Spring
    {
        public float X, V, Target;

        public void Step(float k, float c, float h)
        {
            V += (-k * (X - Target) - c * V) * h;
            X += V * h;
        }

        public readonly bool Moving => MathF.Abs(X - Target) > 1e-4f || MathF.Abs(V) > 1e-3f;

        public void SettleIfStill()
        {
            if (Moving) return;
            X = Target;
            V = 0;
        }
    }

    private enum M { Squish, Stretch, LeanL, LeanR, Breathe, Blink, EyesHappy, EyesSqueeze, MouthSmile, MouthO, MouthGrin, CheeksUp }

    private static readonly string[] MorphNames =
        ["squish", "stretch", "lean_L", "lean_R", "breathe", "blink", "eyes_happy", "eyes_squeeze", "mouth_smile", "mouth_o", "mouth_grin", "cheeks_up"];

    private const int MorphCount = 12;

    private readonly BloopAnimator _anim;
    private readonly bool _clips;
    private readonly int _root, _body, _meshNode;
    private readonly int[] _morph = new int[MorphCount];
    private readonly float[] _mw = new float[MorphCount];
    private readonly float[] _mPeak = new float[MorphCount];
    private readonly double[] _mUntil = new double[MorphCount];
    private readonly float[] _mSustain = new float[MorphCount];
    private readonly TapEscalation _taps = new();
    private readonly Random _random = new();

    private Spring _squash, _roll, _pitch, _yaw, _hop, _dent, _armLeft, _armRight;
    private float _hold, _holdTarget, _melt, _meltTarget;
    private double _shakeStart, _shakeUntil, _swayStart, _swayUntil = double.NegativeInfinity, _settleAt = double.PositiveInfinity;
    private float _shakeAmp, _swayAmp;
    private double _rubUntil, _lastRubSound, _now;
    private int _dentNode = -1;
    private Vector3 _dentDir;
    private bool _patting, _tickling;
    private int _dragArm; // 0 none, 1 viewer's left arm, 2 right
    private Vector2 _dragShoulder, _dragRestHand;
    // Procedural raise or wave for the viewer's-left hand (the high_five and wave clips use the other one).
    private double _armRaiseStart, _armRaiseUntil;
    private bool _armRaiseWave;

    public BloopMotion(GltfModel model, BloopAnimator anim, bool clips)
    {
        _anim = anim;
        _clips = clips;
        _root = model.NodeIndex("root");
        _body = model.NodeIndex("body");
        _meshNode = Array.FindIndex(model.Nodes, n => n.TargetNames.Length > 0);
        var names = _meshNode >= 0 ? model.Nodes[_meshNode].TargetNames : [];
        for (var i = 0; i < MorphCount; i++) _morph[i] = Array.IndexOf(names, MorphNames[i]);
        JellyTop = model.NodeIndex("jelly.top");
        JellyFront = model.NodeIndex("jelly.front");
        JellyBottom = model.NodeIndex("jelly.bottom");
        JellyL = model.NodeIndex("jelly.L");
        JellyR = model.NodeIndex("jelly.R");
        HandL = model.NodeIndex("hand.L");
        HandR = model.NodeIndex("hand.R");
        ArmL = model.NodeIndex("arm.L.1");
        ArmR = model.NodeIndex("arm.R.1");
        Body = _body;
        Root = _root;
    }

    public int JellyTop { get; }
    public int JellyFront { get; }
    public int JellyBottom { get; }
    public int JellyL { get; }
    public int JellyR { get; }
    public int HandL { get; }
    public int HandR { get; }
    public int ArmL { get; }
    public int ArmR { get; }
    public int Body { get; }
    public int Root { get; }

    // Screen helpers supplied by the viewport each reaction: DIP position of a node, and the projected body.
    public Func<int, Vector2>? Project { get; set; }
    public BodyJoints Joints { get; set; }

    public void Reset()
    {
        _squash = _roll = _pitch = _yaw = _hop = _dent = _armLeft = _armRight = default;
        _hold = _holdTarget = _melt = _meltTarget = 0;
        _shakeUntil = _rubUntil = 0;
        _swayUntil = double.NegativeInfinity;
        _settleAt = double.PositiveInfinity;
        _patting = _tickling = false;
        _dragArm = 0;
        _armRaiseUntil = 0;
        Array.Clear(_mw);
        Array.Clear(_mUntil);
        Array.Clear(_mSustain);
    }

    public bool Busy => _armRaiseUntil > 0 ||
        _squash.Moving || _roll.Moving || _pitch.Moving || _yaw.Moving || _hop.Moving || _dent.Moving || _armLeft.Moving || _armRight.Moving
        || _hold > 1e-3f || _holdTarget > 0 || _melt > 1e-3f || _meltTarget > 0 || _now < _shakeUntil || _now < _swayUntil
        || !double.IsPositiveInfinity(_settleAt) || MorphsActive();

    private bool MorphsActive()
    {
        for (var i = 0; i < MorphCount; i++)
            if (_mw[i] > 1e-3f || _mSustain[i] > 0 || _now < _mUntil[i]) return true;
        return false;
    }

    // One 1/240 s spring sub-step.
    public void Step(float h)
    {
        _squash.Step(1935, 26.4f, h);
        _roll.Step(260, 9, h);
        _pitch.Step(260, 9, h);
        _yaw.Step(90, 8, h);
        _hop.Step(420, 14, h);
        _dent.Step(1100, 22, h);
        _armLeft.Step(140, 16, h);
        _armRight.Step(140, 16, h);
    }

    // Once per frame: timers, smoothing and the end of a rub.
    public void Update(double now, float dt)
    {
        _now = now;
        if (now >= _settleAt)
        {
            _settleAt = double.PositiveInfinity;
            // "ok ok!": a little shrug, a happy face, settle down.
            if (_clips) _anim.Interrupt("shoulder_shrug", 0.6f);
            Pulse(M.EyesHappy, 1, 1.0);
            Pulse(M.MouthSmile, 1, 1.2);
            _squash.V -= 5;
            BloopSounds.Play("purr");
        }
        if ((_tickling || _meltTarget > 0) && !_patting && now > _rubUntil) EndRub();
        if (_dragArm == 0 && _armRaiseUntil > 0)
        {
            if (now >= _armRaiseUntil) { _armRaiseUntil = 0; _armLeft.Target = 0; }
            else
            {
                // Viewer's-left arm: a negative turn about the view axis lifts it up and out.
                var up = (float)Math.Min(1, (now - _armRaiseStart) / 0.12);
                var wiggle = _armRaiseWave ? 0.35f * MathF.Sin(2 * MathF.PI * 2.6f * (float)(now - _armRaiseStart)) : 0;
                _armLeft.Target = -(1.9f + wiggle) * up;
            }
        }

        _hold += (_holdTarget - _hold) * Math.Min(1, dt * (_holdTarget > _hold ? 6 : 14));
        _melt += (_meltTarget - _melt) * Math.Min(1, dt * 3);
        if (_hold < 1e-3f && _holdTarget == 0) _hold = 0;
        if (_melt < 1e-3f && _meltTarget == 0) _melt = 0;
        _dent.Target = -0.012f * _hold;

        for (var i = 0; i < MorphCount; i++)
        {
            var target = Math.Max(_mSustain[i], now < _mUntil[i] ? _mPeak[i] : 0);
            var rate = target > _mw[i] ? 14 : 5;
            _mw[i] += (target - _mw[i]) * Math.Min(1, dt * rate);
            if (target == 0 && _mw[i] < 1e-3f) _mw[i] = 0;
        }
        _squash.SettleIfStill();
        _roll.SettleIfStill();
        _pitch.SettleIfStill();
        _yaw.SettleIfStill();
        _hop.SettleIfStill();
        _dent.SettleIfStill();
        _armLeft.SettleIfStill();
        _armRight.SettleIfStill();
    }

    private void Pulse(M m, float peak, double seconds)
    {
        var i = (int)m;
        if (_now >= _mUntil[i]) _mPeak[i] = peak;
        else _mPeak[i] = Math.Max(_mPeak[i], peak);
        _mUntil[i] = Math.Max(_mUntil[i], _now + seconds);
    }

    private void Shake(float amp, double seconds)
    {
        if (_now >= _shakeUntil) _shakeStart = _now;
        _shakeAmp = Math.Max(_now < _shakeUntil ? _shakeAmp : 0, amp);
        _shakeUntil = Math.Max(_shakeUntil, _now + seconds);
    }

    private void Sway(float amp, double seconds)
    {
        if (_now >= _swayUntil) _swayStart = _now;
        _swayAmp = amp;
        _swayUntil = Math.Max(_swayUntil, _now + seconds);
    }

    private void Clip(string name, float weight = 1)
    {
        if (!_clips) return;
        // Re-asking for the clip that just started is a spring impulse only, never a restart from a crossfade
        // between two phases of the same clip (that read as "reacts once, then barely").
        if (_anim.CurrentClip == name && _anim.CurrentProgress < 0.45f) return;
        _anim.Interrupt(name, weight);
    }

    // A visible bump right where the pointer touched: a dent into the nearest jelly part, a squash, and a tilt away.
    private void ContactBump(Vector2 p, BodyRegion region, float strength)
    {
        var j = Joints;
        var n = BodyMap.Normalize(p, j);
        _dentNode = region switch
        {
            BodyRegion.HeadTop => JellyTop,
            BodyRegion.Feet => JellyBottom,
            BodyRegion.LeftHand => LeftIs(HandL) ? HandL : HandR,
            BodyRegion.RightHand => LeftIs(HandL) ? HandR : HandL,
            BodyRegion.Cheeks => (n.X < 0) == LeftIs(JellyL) ? JellyL : JellyR,
            _ => JellyFront,
        };
        _dentDir = region switch
        {
            BodyRegion.HeadTop => new Vector3(0, -1, -0.3f),
            BodyRegion.Feet => new Vector3(0, 0.6f, -1),
            BodyRegion.Cheeks => Vector3.Normalize(new Vector3(-Math.Sign(n.X), 0, -1)),
            _ => new Vector3(-0.25f * n.X, -0.2f, -1),
        };
        _dentDir = Vector3.Normalize(_dentDir);
        _dent.V -= 0.55f * strength;
        _squash.V -= 7 * strength;
        _roll.V += 2.2f * strength * Math.Clamp(n.X, -1, 1) * (n.Y < 0.3f ? 1 : -0.6f);
        _pitch.V -= 1.6f * strength;
    }

    // Stretch with Bloop: the user's pose drives the arms, lean, head tilt and shrug through the same springs the touch
    // reactions use (so it follows through softly). With springs off the pose is applied at once.
    private const float ArmRaiseRadAt180 = 2.7f;

    public void SetMirror(in DeskPal.Core.Pose.MirrorValues v, bool springy)
    {
        _dragArm = 0;
        _armRaiseUntil = 0;
        var raiseL = v.HasPerson ? Math.Clamp(v.LeftArmRaiseDeg, 0, 180) / 180f * ArmRaiseRadAt180 : 0f;
        var raiseR = v.HasPerson ? Math.Clamp(v.RightArmRaiseDeg, 0, 180) / 180f * ArmRaiseRadAt180 : 0f;
        const float rad = MathF.PI / 180f;
        var roll = v.HasPerson ? Math.Clamp(-(v.LeanDeg * 0.6f + v.HeadTiltDeg * 0.4f) * rad, -0.45f, 0.45f) : 0f;
        _armLeft.Target = -raiseL;
        _armRight.Target = raiseR;
        _roll.Target = roll;
        _hop.Target = v.HasPerson ? 0.03f * Math.Clamp(v.Shrug, 0, 1) : 0f;
        _mSustain[(int)M.MouthSmile] = v.HasPerson ? 0.5f : 0f;
        if (!springy)
        {
            _armLeft.X = _armLeft.Target; _armLeft.V = 0;
            _armRight.X = _armRight.Target; _armRight.V = 0;
            _roll.X = roll; _roll.V = 0;
            _hop.X = _hop.Target; _hop.V = 0;
        }
    }

    // Back to the normal rest pose; the springs ease there.
    public void ClearMirror()
    {
        _armLeft.Target = _armRight.Target = _roll.Target = _hop.Target = 0;
        _mSustain[(int)M.MouthSmile] = 0;
    }

    // True when node a is on the viewer's left of its mirror twin.
    private bool LeftIs(int a)
    {
        if (Project is null || a < 0) return a == HandL || a == JellyL;
        var twin = a == HandL ? HandR : a == HandR ? HandL : a == JellyL ? JellyR : JellyL;
        return twin < 0 || Project(a).X <= Project(twin).X;
    }

    public void React(in GestureEvent e, BodyRegion region)
    {
        switch (e.Kind)
        {
            case GestureKind.Tap:
            case GestureKind.MultiTap:
                Tap(e, region);
                break;
            case GestureKind.LongPressStart:
                ContactBump(e.Position, region, 0.6f);
                _holdTarget = 0.3f;
                if (region == BodyRegion.HeadTop)
                {
                    _patting = true;
                    _mSustain[(int)M.EyesHappy] = 1;
                    _mSustain[(int)M.MouthSmile] = 0.7f;
                    _meltTarget = 0.35f;
                    Sway(1, 30);
                    BloopSounds.Play("purr");
                }
                else
                {
                    _mSustain[(int)M.MouthO] = 0.5f;
                    BloopSounds.Play("squish");
                }
                break;
            case GestureKind.Holding:
                _holdTarget = 0.3f + 0.7f * (float)e.Progress;
                if (_patting && _now - _lastRubSound > 1.6)
                {
                    _lastRubSound = _now;
                    BloopSounds.Play("purr");
                }
                break;
            case GestureKind.LongPressEnd:
            {
                var k = (float)Math.Clamp(e.Duration / 2, 0.2, 1);
                _holdTarget = 0;
                _hold *= 0.3f; // let go: the spring takes over from the held depth
                _squash.V += 8 + 18 * k;
                _hop.V += 0.12f + 0.35f * k;
                _mSustain[(int)M.MouthO] = 0;
                if (_patting)
                {
                    _patting = false;
                    _mSustain[(int)M.EyesHappy] = 0;
                    _mSustain[(int)M.MouthSmile] = 0;
                    _meltTarget = 0;
                    _swayUntil = Math.Min(_swayUntil, _now + 0.8);
                    Pulse(M.EyesHappy, 1, 0.8);
                    BloopSounds.Play("purr");
                }
                else
                {
                    Pulse(M.MouthGrin, 0.8f, 0.6);
                    BloopSounds.Play("boing");
                }
                break;
            }
            case GestureKind.Rub:
                Rub(region);
                break;
            case GestureKind.RubEnd:
                EndRub();
                break;
            case GestureKind.Drag:
                Drag(e, region);
                break;
            case GestureKind.DragEnd:
                EndDrag(e);
                break;
            case GestureKind.Swipe:
                Swipe(e, region);
                break;
        }
    }

    private void Tap(in GestureEvent e, BodyRegion region)
    {
        var mood = _taps.For(e.Count, _now);
        var hand = region is BodyRegion.LeftHand or BodyRegion.RightHand;
        switch (mood)
        {
            case TapMood.Settling:
                // Cooldown after the big one: still a small, clearly visible squish.
                ContactBump(e.Position, region, 0.7f);
                Pulse(M.MouthSmile, 0.6f, 0.4);
                BloopSounds.Play("squish");
                return;
            case TapMood.Giggle when hand:
                ContactBump(e.Position, region, 0.6f);
                if (region == BodyRegion.LeftHand) RaiseLeft(wave: true, 1.3);
                else Clip("wave");
                Pulse(M.MouthSmile, 1, 1.2);
                BloopSounds.Play("hop");
                return;
            case TapMood.Giggle:
                ContactBump(e.Position, region, 0.9f);
                Shake(0.5f, 0.6);
                Pulse(M.EyesHappy, 1, 0.9);
                Pulse(M.MouthGrin, 1, 0.9);
                BloopSounds.Play("giggle");
                return;
            case TapMood.Ticklish:
                ContactBump(e.Position, region, 1f);
                Shake(1, 1.2);
                _hop.V += 0.15f;
                Pulse(M.EyesHappy, 1, 1.4);
                Pulse(M.MouthGrin, 1, 1.4);
                Pulse(M.CheeksUp, 1, 1.4);
                BloopSounds.Play("giggle");
                return;
            case TapMood.Dizzy:
                ContactBump(e.Position, region, 1f);
                _yaw.X -= 2 * MathF.PI; // one full spin, sprung back to facing front
                _yaw.V += 2;
                Clip("star_jump");
                Pulse(M.EyesSqueeze, 1, 0.9);
                Pulse(M.MouthO, 0.8f, 0.9);
                _settleAt = _now + 1.8;
                BloopSounds.Play("boing");
                return;
        }

        ContactBump(e.Position, region, 1f);
        switch (region)
        {
            case BodyRegion.Belly:
                _squash.V -= 6;
                Pulse(M.MouthO, 0.8f, 0.5);
                Pulse(M.Squish, 0.6f, 0.3);
                BloopSounds.Play("squish");
                break;
            case BodyRegion.HeadTop:
                _squash.V -= 9;
                _anim.Blink();
                Pulse(M.EyesSqueeze, 0.8f, 0.3);
                BloopSounds.Play("bonk");
                break;
            case BodyRegion.Face:
                _pitch.V -= 3;
                _anim.Blink();
                Pulse(M.EyesSqueeze, 1, 0.55);
                Pulse(M.MouthO, 0.5f, 0.5);
                BloopSounds.Play("squish");
                break;
            case BodyRegion.Cheeks:
            {
                var left = BodyMap.Normalize(e.Position, Joints).X < 0;
                // Lean into the finger.
                _roll.V += left ? 2.5f : -2.5f;
                Pulse(left ? M.LeanL : M.LeanR, 0.8f, 1.0);
                Pulse(M.CheeksUp, 1, 1.4);
                Pulse(M.MouthSmile, 1, 1.4);
                Pulse(M.EyesHappy, 0.8f, 1.2);
                BloopSounds.Play("purr");
                break;
            }
            case BodyRegion.LeftHand:
            case BodyRegion.RightHand:
                if (region == BodyRegion.LeftHand) RaiseLeft(wave: false, 0.7);
                else Clip("high_five");
                Pulse(M.MouthGrin, 1, 1.0);
                BloopSounds.Play("clap");
                break;
            case BodyRegion.Feet:
                _hop.V += 0.38f;
                _squash.V += 10;
                Pulse(M.MouthO, 0.6f, 0.4);
                BloopSounds.Play("hop");
                break;
        }
    }

    private void RaiseLeft(bool wave, double seconds)
    {
        _armRaiseStart = _now;
        _armRaiseUntil = _now + seconds;
        _armRaiseWave = wave;
    }

    private void Rub(BodyRegion region)
    {
        var first = _rubUntil < _now && _meltTarget == 0 && !_tickling;
        _rubUntil = _now + 0.45;
        if (region == BodyRegion.Belly)
        {
            _tickling = true;
            Shake(0.6f, 0.5);
            _mSustain[(int)M.EyesHappy] = 1;
            _mSustain[(int)M.MouthGrin] = 1;
            if (first || _now - _lastRubSound > 0.9)
            {
                _lastRubSound = _now;
                BloopSounds.Play("giggle");
            }
        }
        else
        {
            // Content melt: eyes half closed, a slow sway, a slight flatten.
            _meltTarget = 1;
            _mSustain[(int)M.Blink] = 0.45f;
            _mSustain[(int)M.EyesHappy] = 0.7f;
            _mSustain[(int)M.MouthSmile] = 0.8f;
            Sway(0.8f, 0.9);
            if (first || _now - _lastRubSound > 1.4)
            {
                _lastRubSound = _now;
                BloopSounds.Play("purr");
            }
        }
        if (first) _squash.V -= 4;
    }

    private void EndRub()
    {
        if (!_tickling && _meltTarget == 0) return;
        _tickling = false;
        _meltTarget = 0;
        _mSustain[(int)M.Blink] = 0;
        _mSustain[(int)M.EyesHappy] = 0;
        _mSustain[(int)M.MouthSmile] = 0;
        _mSustain[(int)M.MouthGrin] = 0;
        Pulse(M.EyesHappy, 0.8f, 0.6);
        Pulse(M.MouthSmile, 0.8f, 0.8);
    }

    private void Drag(in GestureEvent e, BodyRegion region)
    {
        if (region is BodyRegion.LeftHand or BodyRegion.RightHand && Project is { } project)
        {
            if (_dragArm == 0)
            {
                _dragArm = region == BodyRegion.LeftHand ? 1 : 2;
                var hand = (region == BodyRegion.LeftHand) == LeftIs(HandL) ? HandL : HandR;
                var arm = hand == HandL ? ArmL : ArmR;
                _dragShoulder = project(arm);
                _dragRestHand = project(hand);
                Pulse(M.MouthO, 0.7f, 0.6);
                BloopSounds.Play("hop");
            }
            // IK-lite: turn the whole arm in the screen plane so the hand points at the pointer.
            var rest = _dragRestHand - _dragShoulder;
            var want = e.Position - _dragShoulder;
            var angle = MathF.Atan2(-want.Y, want.X) - MathF.Atan2(-rest.Y, rest.X);
            while (angle > MathF.PI) angle -= 2 * MathF.PI;
            while (angle < -MathF.PI) angle += 2 * MathF.PI;
            angle = Math.Clamp(angle, -1.8f, 1.8f);
            if (_dragArm == 1) _armLeft.Target = angle; else _armRight.Target = angle;
            return;
        }
        // Anywhere else Bloop's body leans after the pointer like jelly.
        var dx = e.Direction.X / Math.Max(20f, Joints.HalfWidth * 2);
        _roll.Target = Math.Clamp(-dx * 0.7f, -0.35f, 0.35f);
        _pitch.Target = Math.Clamp(e.Direction.Y / Math.Max(20f, Joints.HalfWidth * 4), -0.2f, 0.2f);
        if (_holdTarget > 0) _holdTarget = 0.3f;
    }

    private void EndDrag(in GestureEvent e)
    {
        _holdTarget = 0;
        if (_dragArm != 0)
        {
            _armLeft.Target = _armRight.Target = 0;
            _dragArm = 0;
            Clip("wave");
            Pulse(M.MouthSmile, 1, 1.2);
            BloopSounds.Play("boing");
            return;
        }
        var lean = MathF.Abs(_roll.Target) + MathF.Abs(_pitch.Target);
        _roll.Target = _pitch.Target = 0;
        _squash.V += 6;
        if (lean > 0.05f) BloopSounds.Play("boing");
    }

    private void Swipe(in GestureEvent e, BodyRegion region)
    {
        var s = (float)Math.Clamp(e.Speed / 1500, 0.4, 1.3);
        var d = e.Direction;
        ContactBump(e.Start, region, 0.5f);
        if (MathF.Abs(d.X) >= MathF.Abs(d.Y))
        {
            // Spin the way the swipe went, with jelly follow-through; a fast one goes all the way round.
            var dir = MathF.Sign(d.X);
            if (e.Speed > 1200) _yaw.X -= dir * 2 * MathF.PI;
            _yaw.V += dir * 9 * s;
            _roll.V -= dir * 3 * s;
            Pulse(M.MouthO, 0.8f, 0.6);
            BloopSounds.Play("whoosh");
        }
        else if (d.Y < 0)
        {
            _hop.V += 0.3f * s;
            _squash.V += 14 * s;
            Pulse(M.Stretch, 0.8f, 0.5);
            Pulse(M.MouthSmile, 1, 1.0);
            Clip("stretch_overhead", 0.8f);
            BloopSounds.Play("whoosh");
            BloopSounds.Play("hop");
        }
        else
        {
            _squash.V -= 13 * s;
            _dentNode = JellyTop;
            _dentDir = new Vector3(0, -1, 0);
            _dent.V -= 0.4f * s;
            Pulse(M.Squish, 1, 0.45);
            Pulse(M.EyesSqueeze, 0.7f, 0.4);
            BloopSounds.Play("whoosh");
            BloopSounds.Play("squish");
        }
    }

    // Applies everything to the evaluated pose. world is the previous frame's node world matrices (one frame behind
    // is fine), used to turn world-space offsets and rotations into each node's parent space.
    public void Apply(Pose pose, ReadOnlySpan<Matrix4x4> world, GltfModel model, Vector2 eye)
    {
        var t = (float)_now;
        if (_body >= 0)
        {
            var sy = 1 + Math.Max(_squash.X, -0.4f) - 0.28f * _hold - 0.1f * _melt;
            var sxz = 1 / MathF.Sqrt(Math.Max(0.3f, sy));
            pose.S[_body] *= new Vector3(sxz, sy, sxz);
            var turn = Quaternion.CreateFromYawPitchRoll(0.35f * eye.X, 0.15f * eye.Y, 0);
            pose.R[_body] = Quaternion.Concatenate(pose.R[_body], turn);
        }

        var roll = _roll.X;
        if (_now < _shakeUntil)
        {
            var env = (float)Math.Min(1, (_shakeUntil - _now) / 0.25);
            roll += _shakeAmp * 0.13f * env * MathF.Sin(2 * MathF.PI * 9 * (float)(_now - _shakeStart));
        }
        if (_now < _swayUntil)
        {
            var env = (float)Math.Min(Math.Min(1, (_swayUntil - _now) / 0.6), (_now - _swayStart) / 0.4);
            roll += _swayAmp * 0.09f * env * MathF.Sin(2 * MathF.PI * 0.7f * (float)(_now - _swayStart));
        }
        if (_root >= 0 && world.Length > _root)
        {
            var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll)
                        * Quaternion.CreateFromAxisAngle(Vector3.UnitX, _pitch.X)
                        * Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw.X);
            RotateWorld(pose, world, model, _root, delta);
            MoveWorld(pose, world, model, _root, new Vector3(0, Math.Max(_hop.X, -0.002f), 0));
        }
        if (_dentNode >= 0 && MathF.Abs(_dent.X) > 1e-5f) MoveWorld(pose, world, model, _dentNode, _dentDir * -_dent.X);
        if (ArmL >= 0 && ArmR >= 0 && MathF.Abs(_armLeft.X) > 1e-4f) RotateWorld(pose, world, model, ArmFor(1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _armLeft.X));
        if (ArmR >= 0 && MathF.Abs(_armRight.X) > 1e-4f) RotateWorld(pose, world, model, ArmFor(2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _armRight.X));

        if (_meshNode >= 0)
        {
            var w = pose.W[_meshNode];
            for (var i = 0; i < MorphCount; i++)
            {
                var k = _morph[i];
                if (k < 0 || k >= w.Length || _mw[i] <= 0) continue;
                w[k] = Math.Clamp(Math.Max(w[k], _mw[i]), 0, 1);
            }
            var flat = _morph[(int)M.Squish];
            if (flat >= 0 && flat < w.Length) w[flat] = Math.Clamp(w[flat] + 0.5f * _melt + 0.6f * _hold, 0, 1);
        }
        _ = t;
    }

    private int ArmFor(int side) => (side == 1) == LeftIs(HandL) ? ArmL : ArmR;

    private static Quaternion ParentRotation(ReadOnlySpan<Matrix4x4> world, GltfModel model, int node)
    {
        var p = model.Nodes[node].Parent;
        if (p < 0 || p >= world.Length) return Quaternion.Identity;
        return Matrix4x4.Decompose(world[p], out _, out var r, out _) ? Quaternion.Normalize(r) : Quaternion.Identity;
    }

    // local' = (P^-1 * delta * P) * local, so the node turns by delta in world space about its own origin.
    private static void RotateWorld(Pose pose, ReadOnlySpan<Matrix4x4> world, GltfModel model, int node, Quaternion delta)
    {
        if (node < 0) return;
        var p = ParentRotation(world, model, node);
        var local = Quaternion.Inverse(p) * delta * p;
        pose.R[node] = Quaternion.Normalize(local * pose.R[node]);
    }

    private static void MoveWorld(Pose pose, ReadOnlySpan<Matrix4x4> world, GltfModel model, int node, Vector3 offset)
    {
        if (node < 0) return;
        var p = model.Nodes[node].Parent;
        if (p < 0 || p >= world.Length) { pose.T[node] += offset; return; }
        if (!Matrix4x4.Invert(world[p], out var inv)) return;
        pose.T[node] += Vector3.TransformNormal(offset, inv);
    }
}
