using System.Numerics;

namespace DeskPal.Core;

// Where on Bloop a pointer landed. Left and right are as the viewer sees them (screen left / screen right).
public enum BodyRegion { None, HeadTop, Face, Cheeks, Belly, LeftHand, RightHand, Feet }

public enum GestureKind
{
    Tap,            // a quick press and release; Count is 1
    MultiTap,       // a tap within the multi-tap gap of the previous one; Count is 2, 3, ...
    LongPressStart, // held still past the long-press time
    Holding,        // still held after LongPressStart; Progress runs 0..1 over the next 1.5 s
    LongPressEnd,   // released after a long press without moving; Duration is the whole hold
    Drag,           // moving while down (Held is true when it started as a long press)
    DragEnd,
    Rub,            // back-and-forth strokes over the same area; Count is the number of direction changes
    RubEnd,
    Swipe,          // a fast stroke; Direction is a unit vector (screen space, y down), Speed in DIP/s
}

public readonly record struct GestureEvent(
    GestureKind Kind,
    Vector2 Position,
    Vector2 Start,
    int Count = 0,
    double Duration = 0,
    double Progress = 0,
    Vector2 Direction = default,
    double Speed = 0,
    bool Held = false);

// Turns pointer samples (positions in DIPs, times in seconds) into gestures. No UI types and no clock of its own,
// so the same samples always give the same gestures. Tick should be called while a pointer is down so a long press
// is noticed without movement.
public sealed class GestureClassifier
{
    public const double LongPressSeconds = 0.45;
    public const double MultiTapGapSeconds = 0.35;
    public const double HoldRampSeconds = 1.5;

    private readonly float _slop;
    private readonly float _swipeDistance;
    private readonly float _swipeSpeed;
    private readonly float _rubSegment;
    private readonly float _multiTapRadius;

    private const int SampleCount = 8;
    private readonly Vector2[] _samplePos = new Vector2[SampleCount];
    private readonly double[] _sampleTime = new double[SampleCount];
    private int _sampleNext, _samples;

    private bool _down;
    private Vector2 _start, _last;
    private double _downTime;
    private float _path;
    private bool _moving, _longPress, _rubbing;
    private int _reversals;
    private int _axisSign;
    private Vector2 _segmentStart;

    private int _tapCount;
    private double _lastTapUp = double.NegativeInfinity;
    private Vector2 _lastTapPos;

    // pixelsPerDip is the display scale (1.0 at 100 %). Movement thresholds never fall under a few physical pixels,
    // so a shaky hand on a low-DPI screen still taps.
    public GestureClassifier(double pixelsPerDip = 1)
    {
        var px = (float)Math.Max(0.5, pixelsPerDip);
        _slop = Math.Max(7f, 10f / px);
        _swipeDistance = Math.Max(26f, 36f / px);
        _swipeSpeed = 450f;
        _rubSegment = Math.Max(8f, 12f / px);
        _multiTapRadius = Math.Max(36f, 48f / px);
    }

    public bool IsDown => _down;

    public float Slop => _slop;

    public GestureEvent? Down(Vector2 p, double t)
    {
        _down = true;
        _start = _last = _segmentStart = p;
        _downTime = t;
        _path = 0;
        _moving = _longPress = _rubbing = false;
        _reversals = 0;
        _axisSign = 0;
        _samples = _sampleNext = 0;
        AddSample(p, t);
        return null;
    }

    public GestureEvent? Move(Vector2 p, double t)
    {
        if (!_down) return null;
        _path += Vector2.Distance(p, _last);
        _last = p;
        AddSample(p, t);
        if (!_moving && Vector2.Distance(p, _start) > _slop)
        {
            _moving = true;
            _segmentStart = _start;
        }
        if (!_moving) return Tick(t);

        // Rub: count direction changes along the stroke's main axis, ignoring tiny wiggles.
        var seg = p - _segmentStart;
        var horizontal = Math.Abs(seg.X) >= Math.Abs(seg.Y);
        var along = horizontal ? seg.X : seg.Y;
        if (Math.Abs(along) >= _rubSegment)
        {
            var sign = Math.Sign(along);
            if (_axisSign != 0 && sign != _axisSign)
            {
                _reversals++;
                if (_reversals >= 2) _rubbing = true;
                _axisSign = sign;
                _segmentStart = p;
                if (_rubbing) return new GestureEvent(GestureKind.Rub, p, _start, Count: _reversals, Duration: t - _downTime);
            }
            else
            {
                _axisSign = sign;
                _segmentStart = p;
            }
        }
        if (_rubbing) return new GestureEvent(GestureKind.Rub, p, _start, Count: _reversals, Duration: t - _downTime);
        return new GestureEvent(GestureKind.Drag, p, _start, Duration: t - _downTime, Direction: p - _start, Held: _longPress);
    }

    public GestureEvent? Tick(double t)
    {
        if (!_down || _moving) return null;
        var held = t - _downTime;
        if (held < LongPressSeconds) return null;
        if (!_longPress)
        {
            _longPress = true;
            return new GestureEvent(GestureKind.LongPressStart, _last, _start, Duration: held);
        }
        var progress = Math.Clamp((held - LongPressSeconds) / HoldRampSeconds, 0, 1);
        return new GestureEvent(GestureKind.Holding, _last, _start, Duration: held, Progress: progress);
    }

    public GestureEvent? Up(Vector2 p, double t)
    {
        if (!_down) return null;
        var moved = Move(p, t);
        _down = false;
        var held = t - _downTime;
        if (_rubbing) return new GestureEvent(GestureKind.RubEnd, p, _start, Count: _reversals, Duration: held);
        if (_moving)
        {
            var (dir, speed) = RecentVelocity(t);
            var travel = Vector2.Distance(p, _start);
            if (!_longPress && travel >= _swipeDistance && speed >= _swipeSpeed)
            {
                var overall = p - _start;
                var direction = overall.LengthSquared() > 1e-6f ? Vector2.Normalize(overall) : dir;
                return new GestureEvent(GestureKind.Swipe, p, _start, Duration: held, Direction: direction, Speed: speed);
            }
            return new GestureEvent(GestureKind.DragEnd, p, _start, Duration: held, Direction: p - _start, Held: _longPress);
        }
        if (_longPress || held >= LongPressSeconds)
        {
            _tapCount = 0;
            _lastTapUp = double.NegativeInfinity;
            return new GestureEvent(GestureKind.LongPressEnd, p, _start, Duration: held,
                Progress: Math.Clamp((held - LongPressSeconds) / HoldRampSeconds, 0, 1));
        }
        _ = moved;
        var chained = _downTime - _lastTapUp <= MultiTapGapSeconds && Vector2.Distance(_start, _lastTapPos) <= _multiTapRadius;
        _tapCount = chained ? _tapCount + 1 : 1;
        _lastTapUp = t;
        _lastTapPos = _start;
        return new GestureEvent(_tapCount > 1 ? GestureKind.MultiTap : GestureKind.Tap, p, _start, Count: _tapCount, Duration: held);
    }

    // The pointer left or capture was lost: ends whatever was running without counting it as a tap.
    public GestureEvent? Cancel(double t)
    {
        if (!_down) return null;
        _down = false;
        if (_rubbing) return new GestureEvent(GestureKind.RubEnd, _last, _start, Count: _reversals, Duration: t - _downTime);
        if (_moving) return new GestureEvent(GestureKind.DragEnd, _last, _start, Duration: t - _downTime, Held: _longPress);
        if (_longPress) return new GestureEvent(GestureKind.LongPressEnd, _last, _start, Duration: t - _downTime);
        return null;
    }

    private void AddSample(Vector2 p, double t)
    {
        _samplePos[_sampleNext] = p;
        _sampleTime[_sampleNext] = t;
        _sampleNext = (_sampleNext + 1) % SampleCount;
        if (_samples < SampleCount) _samples++;
    }

    // Velocity over the last ~100 ms of samples, so a stroke that slowed to a stop is not a swipe.
    private (Vector2 Direction, float Speed) RecentVelocity(double now)
    {
        if (_samples < 2) return (Vector2.Zero, 0);
        var newest = (_sampleNext - 1 + SampleCount) % SampleCount;
        var oldest = newest;
        for (var i = 1; i < _samples; i++)
        {
            var k = (newest - i + SampleCount) % SampleCount;
            oldest = k;
            if (now - _sampleTime[k] >= 0.1) break;
        }
        var dt = _sampleTime[newest] - _sampleTime[oldest];
        if (dt <= 1e-4) return (Vector2.Zero, 0);
        var d = _samplePos[newest] - _samplePos[oldest];
        var len = d.Length();
        return (len > 1e-6f ? d / len : Vector2.Zero, (float)(len / dt));
    }
}

// Screen positions (DIPs) of Bloop's key points in the current pose; Left/Right hands are as the viewer sees them.
public readonly record struct BodyJoints(Vector2 Top, Vector2 Bottom, Vector2 Center, float HalfWidth,
    Vector2 LeftHand, Vector2 RightHand, float HandRadius);

public static class BodyMap
{
    // n is relative to Bloop's body: x from -1 (viewer's left edge) to 1, y from -1 (top of the head) to 1 (bottom).
    public static BodyRegion FromNormalized(Vector2 n)
    {
        var ax = Math.Abs(n.X);
        if (ax > 0.8f && ax < 1.4f && n.Y > -0.2f && n.Y < 0.95f) return n.X < 0 ? BodyRegion.LeftHand : BodyRegion.RightHand;
        if (n.X * n.X + n.Y * n.Y > 1.2f * 1.2f) return BodyRegion.None;
        if (n.Y < -0.5f) return BodyRegion.HeadTop;
        if (n.Y > 0.7f) return BodyRegion.Feet;
        if (ax >= 0.42f && n.Y > -0.3f && n.Y < 0.45f) return BodyRegion.Cheeks;
        if (n.Y < 0.35f) return BodyRegion.Face;
        return BodyRegion.Belly;
    }

    // Normalized point for a screen point, using the projected joints.
    public static Vector2 Normalize(Vector2 p, in BodyJoints j)
    {
        var half = Math.Max(1f, (j.Bottom.Y - j.Top.Y) / 2);
        var midY = (j.Top.Y + j.Bottom.Y) / 2;
        return new Vector2((p.X - j.Center.X) / Math.Max(1f, j.HalfWidth), (p.Y - midY) / half);
    }

    // A hand wins when the pointer is within reach of it (the arms move, so their projected joints decide);
    // otherwise the body zones apply.
    public static BodyRegion FromJoints(Vector2 p, in BodyJoints j)
    {
        var dl = Vector2.Distance(p, j.LeftHand);
        var dr = Vector2.Distance(p, j.RightHand);
        if (Math.Min(dl, dr) <= j.HandRadius) return dl <= dr ? BodyRegion.LeftHand : BodyRegion.RightHand;
        var r = FromNormalized(Normalize(p, j));
        // Away from the hands, the side zones are arm or empty space, not a hand.
        if (r is BodyRegion.LeftHand or BodyRegion.RightHand)
            return Math.Min(dl, dr) <= j.HandRadius * 2.2f ? (dl <= dr ? BodyRegion.LeftHand : BodyRegion.RightHand) : BodyRegion.None;
        return r;
    }

    // Where a synthetic gesture should land for a region, in normalized body space.
    public static Vector2 AnchorFor(BodyRegion r) => r switch
    {
        BodyRegion.HeadTop => new(0, -0.75f),
        BodyRegion.Face => new(0, -0.1f),
        BodyRegion.Cheeks => new(0.55f, 0.15f),
        BodyRegion.Belly => new(0, 0.55f),
        BodyRegion.LeftHand => new(-1f, 0.4f),
        BodyRegion.RightHand => new(1f, 0.4f),
        BodyRegion.Feet => new(0, 0.85f),
        _ => new(0, 0),
    };

    public static Vector2 Denormalize(Vector2 n, in BodyJoints j)
    {
        var half = (j.Bottom.Y - j.Top.Y) / 2;
        var midY = (j.Top.Y + j.Bottom.Y) / 2;
        return new Vector2(j.Center.X + n.X * j.HalfWidth, midY + n.Y * half);
    }
}

// Escalation for repeated taps: friendly at every level, with a short cooldown after the big one. Taps during the
// cooldown still get a small squish so Bloop never feels dead.
public enum TapMood { Single, Giggle, Ticklish, Dizzy, Settling }

public sealed class TapEscalation
{
    public const double CooldownSeconds = 3;
    private double _cooldownUntil = double.NegativeInfinity;

    public TapMood For(int count, double now)
    {
        if (now < _cooldownUntil) return TapMood.Settling;
        switch (count)
        {
            case <= 1: return TapMood.Single;
            case 2: return TapMood.Giggle;
            case <= 4: return TapMood.Ticklish;
            default:
                _cooldownUntil = now + CooldownSeconds;
                return TapMood.Dizzy;
        }
    }
}
