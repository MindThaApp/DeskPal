using System.Diagnostics;
using System.Numerics;
using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskPal;

// Touch reactions for the flat and soft 3D pictures of Bloop: the same gestures and regions as the live model, shown
// with springs on a CompositeTransform (squash, lean, wobble, spin, hop) and short swaps to the other pose pictures.
// Frames run only while something moves.
internal sealed class Bloop2D : IBloopReactor
{
    // The SVG view box is 160 x 140 with the body ellipse (rx 42, ry 36) centred 80 / 80 from its top-left corner.
    private const float BoxW = 160, BoxH = 140, BodyX = 80, BodyY = 80, Rx = 42, Ry = 36;

    private readonly Image _image;
    private readonly FrameworkElement _space;
    private readonly CompositeTransform _t;
    private readonly Func<string> _style;
    private readonly int _pixels;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TapEscalation _taps = new();
    private readonly Dictionary<string, ImageSource?> _poses = [];
    private bool _running;
    private double _last;
    private float _sq, _sqV, _rot, _rotV, _rotTarget, _hop, _hopV, _skew, _skewV, _hold, _holdTarget, _shakeAmp, _swayAmp;
    private double _shakeUntil, _swayUntil, _poseUntil, _rubUntil, _lastSound;
    private ImageSource? _idle;
    private long _poseVersion;

    public Bloop2D(Image image, FrameworkElement space, Func<string> style, int pixels)
    {
        _image = image;
        _space = space;
        _style = style;
        _pixels = pixels;
        _t = new CompositeTransform();
        image.RenderTransform = _t;
        image.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
    }

    // A wave of the picture's hand, for greetings (the touch path does this on a hand tap).
    public void Wave() => Pose("bloop-wave", 1.6);

    public bool Ready => _image.Opacity > 0.5 && _image.Source is not null && _image.ActualWidth > 0;

    private double Now => _clock.Elapsed.TotalSeconds;

    // The picture's body ellipse in the touch space's DIPs (Uniform stretch inside the Image element).
    private BodyJoints Body()
    {
        var w = (float)_image.ActualWidth;
        var h = (float)_image.ActualHeight;
        var s = Math.Min(w / BoxW, h / BoxH);
        var origin = _image.TransformToVisual(_space).TransformPoint(new Windows.Foundation.Point(0, 0));
        // The transform is ours; undo it so regions do not move under a squashing picture.
        var ox = (float)origin.X - (float)_t.TranslateX;
        var oy = (float)origin.Y - (float)_t.TranslateY;
        var left = ox + (w - BoxW * s) / 2;
        var top = oy + (h - BoxH * s) / 2;
        var c = new Vector2(left + BodyX * s, top + BodyY * s);
        var hands = new Vector2(Rx * 1.05f * s, 18 * s);
        return new BodyJoints(c - new Vector2(0, Ry * s), c + new Vector2(0, Ry * s), c, Rx * s,
            c + new Vector2(-hands.X, hands.Y), c + new Vector2(hands.X, hands.Y), 10 * s);
    }

    public BodyRegion RegionAt(Vector2 p) => BodyMap.FromNormalized(BodyMap.Normalize(p, Body()));

    public Vector2? PointFor(BodyRegion region) => BodyMap.Denormalize(BodyMap.AnchorFor(region), Body());

    public void React(in GestureEvent e, BodyRegion region)
    {
        var now = Now;
        var j = Body();
        var n = BodyMap.Normalize(e.Start, j);
        switch (e.Kind)
        {
            case GestureKind.Tap:
            case GestureKind.MultiTap:
            {
                // Every tap: a bump at the contact point (lean away, squash).
                _sqV -= 2.2f;
                _skewV += 90 * Math.Clamp(n.X, -1, 1);
                var mood = _taps.For(e.Count, now);
                var hand = region is BodyRegion.LeftHand or BodyRegion.RightHand;
                switch (mood)
                {
                    case TapMood.Settling:
                        Sound("squish");
                        break;
                    case TapMood.Giggle when hand:
                        Pose("bloop-wave", 1.0);
                        Sound("hop");
                        break;
                    case TapMood.Giggle:
                        Shake(0.6f, 0.6);
                        Sound("giggle");
                        break;
                    case TapMood.Ticklish:
                        Shake(1, 1.2);
                        _hopV -= 120;
                        Pose("bloop-stretch-shoulder-shrug", 1.0);
                        Sound("giggle");
                        break;
                    case TapMood.Dizzy:
                        _rot -= 360;
                        _hopV -= 220;
                        Sound("boing");
                        break;
                    default:
                        switch (region)
                        {
                            case BodyRegion.HeadTop: _sqV -= 3f; Sound("bonk"); break;
                            case BodyRegion.Face: _rotV += 60 * Math.Sign(n.X + 0.01f); Sound("squish"); break;
                            case BodyRegion.Cheeks: _rotTarget = 0; _rotV += n.X < 0 ? -120 : 120; Sound("purr"); break;
                            case BodyRegion.LeftHand:
                            case BodyRegion.RightHand: Pose("bloop-wave", 1.1); Sound("clap"); break;
                            case BodyRegion.Feet: _hopV -= 300; _sqV += 2; Sound("hop"); break;
                            default: _sqV -= 1.5f; Sound("squish"); break;
                        }
                        break;
                }
                break;
            }
            case GestureKind.LongPressStart:
                _holdTarget = 0.3f;
                if (region == BodyRegion.HeadTop) Sway(30);
                Sound(region == BodyRegion.HeadTop ? "purr" : "squish");
                break;
            case GestureKind.Holding:
                _holdTarget = 0.3f + 0.7f * (float)e.Progress;
                break;
            case GestureKind.LongPressEnd:
                _holdTarget = 0;
                _swayUntil = Math.Min(_swayUntil, now + 0.6);
                _sqV += 2 + 4 * (float)Math.Clamp(e.Duration / 2, 0.2, 1);
                Sound(region == BodyRegion.HeadTop ? "purr" : "boing");
                break;
            case GestureKind.Rub:
                _rubUntil = now + 0.45;
                if (region == BodyRegion.Belly) { Shake(0.5f, 0.5); Sound("giggle", 0.9); }
                else { Sway(0.9); _holdTarget = 0.15f; Sound("purr", 1.4); }
                break;
            case GestureKind.RubEnd:
                _holdTarget = 0;
                break;
            case GestureKind.Drag:
                if (region is BodyRegion.LeftHand or BodyRegion.RightHand) Pose("bloop-stretch-overhead-reach", 0.4);
                _rotTarget = Math.Clamp(e.Direction.X / Math.Max(20, j.HalfWidth * 2) * 25, -18, 18);
                break;
            case GestureKind.DragEnd:
                if (region is BodyRegion.LeftHand or BodyRegion.RightHand) { Pose("bloop-wave", 1.0); Sound("boing"); }
                _rotTarget = 0;
                _holdTarget = 0;
                break;
            case GestureKind.Swipe:
                Sound("whoosh");
                if (Math.Abs(e.Direction.X) >= Math.Abs(e.Direction.Y))
                {
                    if (e.Speed > 1200) _rot -= 360 * Math.Sign(e.Direction.X);
                    _rotV += 500 * Math.Sign(e.Direction.X);
                }
                else if (e.Direction.Y < 0) { _hopV -= 260; _sqV += 3; Pose("bloop-stretch-overhead-reach", 0.9); Sound("hop"); }
                else _sqV -= 4;
                break;
        }
        if (e.Kind is not (GestureKind.Holding or GestureKind.Drag or GestureKind.Rub)) Logger.Log($"2d bloop {e.Kind} on {region}");
        Start();
    }

    private void Sound(string name, double minGap = 0)
    {
        if (Now - _lastSound < minGap) return;
        _lastSound = Now;
        BloopSounds.Play(name);
    }

    private void Shake(float amp, double seconds)
    {
        _shakeAmp = amp;
        _shakeUntil = Math.Max(_shakeUntil, Now + seconds);
    }

    private void Sway(double seconds) => _swayUntil = Math.Max(_swayUntil, Now + seconds);

    private async void Pose(string name, double seconds)
    {
        try
        {
            var version = ++_poseVersion;
            _idle ??= _image.Source;
            if (!_poses.TryGetValue(name, out var src))
                _poses[name] = src = await NudgeCardWindow.LoadAvatarAsync(_style(), name, _pixels);
            if (src is null || version != _poseVersion) return;
            _image.Source = src;
            _poseUntil = Now + seconds;
            Start();
        }
        catch (Exception ex)
        {
            Logger.Log("2d bloop pose failed: " + ex.Message);
        }
    }

    // The idle picture changed (style switch): forget cached poses.
    public void ResetPictures()
    {
        _poses.Clear();
        _idle = null;
        _poseVersion++;
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _last = Now;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, object e) => TickGuard.Run("bloop-2d-frame", () =>
    {
        var now = Now;
        var dt = Math.Min(now - _last, 0.1);
        _last = now;
        const double h = 1.0 / 240;
        for (var acc = dt; acc >= h; acc -= h)
        {
            _sqV += (float)((-1500 * _sq - 24 * _sqV) * h); _sq += (float)(_sqV * h);
            _rotV += (float)((-140 * (_rot - _rotTarget) - 10 * _rotV) * h); _rot += (float)(_rotV * h);
            _hopV += (float)((-380 * _hop - 16 * _hopV) * h); _hop += (float)(_hopV * h);
            _skewV += (float)((-600 * _skew - 18 * _skewV) * h); _skew += (float)(_skewV * h);
        }
        if (_rubUntil > 0 && now > _rubUntil && _holdTarget > 0 && _holdTarget < 0.2f) { _holdTarget = 0; _rubUntil = 0; }
        _hold += (float)((_holdTarget - _hold) * Math.Min(1, dt * 8));

        var rot = _rot;
        if (now < _shakeUntil) rot += _shakeAmp * 7 * (float)Math.Sin(2 * Math.PI * 9 * now);
        if (now < _swayUntil) rot += 5 * (float)Math.Sin(2 * Math.PI * 0.7 * now);

        var w = _image.ActualWidth;
        var hgt = _image.ActualHeight;
        var sy = 1 + Math.Max(_sq, -0.4f) * 0.12f - 0.22f * _hold;
        _t.CenterX = w / 2;
        _t.CenterY = hgt * 0.8;
        _t.ScaleY = sy;
        _t.ScaleX = 1 / Math.Sqrt(Math.Max(0.4, sy));
        _t.Rotation = rot;
        _t.SkewX = Math.Clamp(_skew * 0.1, -14, 14);
        _t.TranslateY = Math.Min(_hop * 0.1, 2);

        if (_poseUntil > 0 && now > _poseUntil)
        {
            _poseUntil = 0;
            if (_idle is not null) _image.Source = _idle;
        }

        var moving = Math.Abs(_sq) > 1e-3 || Math.Abs(_sqV) > 1e-2 || Math.Abs(_rot - _rotTarget) > 0.05 || Math.Abs(_rotV) > 0.5
            || Math.Abs(_hop) > 0.05 || Math.Abs(_hopV) > 0.5 || Math.Abs(_skew) > 0.05 || Math.Abs(_skewV) > 0.5
            || Math.Abs(_hold - _holdTarget) > 1e-3 || _hold > 1e-3 || now < _shakeUntil || now < _swayUntil || _poseUntil > 0;
        if (moving) return;
        _sq = _sqV = _rotV = _hop = _hopV = _skew = _skewV = _hold = 0;
        _rot = _rotTarget;
        _t.ScaleX = _t.ScaleY = 1;
        _t.Rotation = _rot;
        _t.SkewX = _t.TranslateY = 0;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    });
}
