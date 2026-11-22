namespace DeskPal.Core.Pose;

/// <summary>Smoothed body points, one slot per <see cref="BodyPoint"/>. X is already multiplied by the image aspect so x and y share one unit.</summary>
public sealed class SmoothedPose
{
    public float[] X { get; } = new float[PoseFrame.PointCount];
    public float[] Y { get; } = new float[PoseFrame.PointCount];
    public bool[] Valid { get; } = new bool[PoseFrame.PointCount];
    public TimeSpan Time { get; internal set; }

    public bool Has(BodyPoint p) => Valid[(int)p];
}

/// <summary>
/// Light temporal smoothing (One Euro filter per coordinate). Points under the score threshold count as missing;
/// a point that goes missing keeps its last smoothed position for <see cref="DropoutHold"/> so brief dropouts do not flicker.
/// Reuses its buffers, so a frame costs no allocations.
/// </summary>
public sealed class PoseSmoother
{
    public const float DefaultScoreThreshold = 0.3f;
    public static readonly TimeSpan DropoutHold = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RestartGap = TimeSpan.FromSeconds(1);

    private const float MinCutoff = 1.5f;   // Hz: heavy smoothing while still
    private const float Beta = 8f;          // opens up with speed (coordinates are 0..1)
    private const float DerivCutoff = 1f;

    private readonly float _aspect;
    private readonly float _threshold;
    private readonly float[] _val = new float[PoseFrame.PointCount * 2];
    private readonly float[] _deriv = new float[PoseFrame.PointCount * 2];
    private readonly TimeSpan[] _seen = new TimeSpan[PoseFrame.PointCount];
    private readonly bool[] _has = new bool[PoseFrame.PointCount];
    private TimeSpan _prevTime;
    private bool _hasPrevTime;

    /// <param name="aspect">Camera image width divided by height, so horizontal and vertical distances compare fairly.</param>
    public PoseSmoother(float aspect = 1f, float scoreThreshold = DefaultScoreThreshold)
    {
        _aspect = aspect <= 0 ? 1f : aspect;
        _threshold = scoreThreshold;
        Current = new SmoothedPose();
    }

    public SmoothedPose Current { get; }

    public void Reset()
    {
        Array.Clear(_has);
        Array.Clear(Current.Valid);
        _hasPrevTime = false;
    }

    /// <summary>Feeds one frame and returns the (reused) smoothed result.</summary>
    public SmoothedPose Update(PoseFrame frame)
    {
        var t = frame.Time;
        float dt = _hasPrevTime ? (float)(t - _prevTime).TotalSeconds : 0f;
        if (dt <= 0f) dt = 1f / 15f;
        if (dt > 1f) dt = 1f;
        _prevTime = t;
        _hasPrevTime = true;
        Current.Time = t;

        for (int i = 0; i < PoseFrame.PointCount; i++)
        {
            var k = frame.Points[i];
            bool seenNow = k.Score >= _threshold && !float.IsNaN(k.X) && !float.IsNaN(k.Y);
            if (seenNow)
            {
                if (_has[i] && t - _seen[i] > RestartGap) _has[i] = false;
                float x = Filter(i * 2, k.X, dt, _has[i]);
                float y = Filter(i * 2 + 1, k.Y, dt, _has[i]);
                _has[i] = true;
                _seen[i] = t;
                Current.X[i] = x * _aspect;
                Current.Y[i] = y;
                Current.Valid[i] = true;
            }
            else
            {
                Current.Valid[i] = _has[i] && t - _seen[i] <= DropoutHold;
            }
        }
        return Current;
    }

    private float Filter(int slot, float raw, float dt, bool primed)
    {
        if (!primed)
        {
            _val[slot] = raw;
            _deriv[slot] = 0f;
            return raw;
        }
        float dRaw = (raw - _val[slot]) / dt;
        _deriv[slot] += Alpha(DerivCutoff, dt) * (dRaw - _deriv[slot]);
        float cutoff = MinCutoff + Beta * MathF.Abs(_deriv[slot]);
        _val[slot] += Alpha(cutoff, dt) * (raw - _val[slot]);
        return _val[slot];
    }

    private static float Alpha(float cutoff, float dt)
    {
        float tau = 1f / (2f * MathF.PI * cutoff);
        return 1f / (1f + tau / dt);
    }
}
