namespace DeskPal.Core.Pose;

public enum StretchState
{
    /// <summary>No usable shoulders in view.</summary>
    NoPerson,
    /// <summary>Person in view but not in the stretch pose.</summary>
    GetReady,
    /// <summary>In the pose, settling for a moment before the ring starts to fill.</summary>
    InPose,
    /// <summary>The ring is filling.</summary>
    Holding,
    /// <summary>Stretch completed. Sticky.</summary>
    Done,
}

/// <summary>
/// Watches one stretch run. Feed it smoothed frames; read <see cref="State"/>, <see cref="Progress"/> and <see cref="Reason"/>.
/// <para>
/// Rules: the pose must be held for <see cref="SettleTime"/> before the ring starts (InPose, then Holding).
/// Once holding, losing the pose for up to <see cref="GraceTime"/> freezes progress and keeps the state at Holding.
/// Past the grace, the state drops to GetReady (or NoPerson) and unfinished progress drains at
/// <see cref="DecayRate"/> times real time, so waving in and out of the pose does not add up.
/// Two-sided stretches (side-bend, neck-roll, seated-twist) need half the hold time on each side; a finished side
/// never drains, and holding the same side again asks for the other one (Pose_SwitchSides).
/// </para>
/// </summary>
public sealed class StretchCheck
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan GraceTime = TimeSpan.FromMilliseconds(500);
    public const float DecayRate = 0.5f;
    public const string NoPersonReason = "Pose_StepIntoView";
    public const string SwitchSidesReason = "Pose_SwitchSides";

    private readonly PoseChecker _checker;
    private readonly PoseSmoother _smoother;
    private readonly float[] _held = new float[2];
    private readonly float _perSide;
    private float _settled;
    private float _lost;
    private bool _holding;
    private TimeSpan _lastTime;
    private bool _hasLast;

    /// <summary>The stretch can be judged from a seated webcam view. Others fall back to the normal Done button.</summary>
    public static bool IsCheckable(string stretchId) => PoseChecker.For(stretchId) is not null;

    /// <summary>Default hold: 40% of the stretch seconds, between 5 and 15 seconds.</summary>
    public static TimeSpan DefaultHold(int stretchSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(stretchSeconds * 0.4, 5, 15));

    /// <summary>Returns null when the stretch is not checkable.</summary>
    public static StretchCheck? Create(string stretchId, int stretchSeconds, TimeSpan? holdTarget = null, float aspect = 1f) =>
        IsCheckable(stretchId) ? new StretchCheck(stretchId, stretchSeconds, holdTarget, aspect) : null;

    /// <param name="holdTarget">Total hold time needed (across both sides for two-sided stretches).</param>
    /// <param name="aspect">Camera image width / height.</param>
    public StretchCheck(string stretchId, int stretchSeconds, TimeSpan? holdTarget = null, float aspect = 1f)
    {
        _checker = PoseChecker.For(stretchId) ?? throw new ArgumentException("Stretch is not checkable: " + stretchId, nameof(stretchId));
        HoldTarget = holdTarget ?? DefaultHold(stretchSeconds);
        if (HoldTarget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(holdTarget));
        _perSide = (float)HoldTarget.TotalSeconds / _checker.Sides;
        _smoother = new PoseSmoother(aspect);
        Reason = NoPersonReason;
    }

    public TimeSpan HoldTarget { get; }
    public StretchState State { get; private set; } = StretchState.NoPerson;
    /// <summary>Hold progress 0..1.</summary>
    public float Progress { get; private set; }
    /// <summary>Resource key explaining what to change, or null when in pose, holding or done.</summary>
    public string? Reason { get; private set; }
    /// <summary>For two-sided stretches, progress 0..1 on each side (0 = user's left, 1 = right).</summary>
    public float SideProgress(int side) => side < _checker.Sides ? Math.Min(1f, _held[side] / _perSide) : 0f;
    public int Sides => _checker.Sides;

    public StretchState Update(PoseFrame frame)
    {
        if (State == StretchState.Done) return State;
        var pose = _smoother.Update(frame);

        float dt = _hasLast ? (float)(frame.Time - _lastTime).TotalSeconds : 0f;
        dt = Math.Clamp(dt, 0f, 0.5f);
        _lastTime = frame.Time;
        _hasLast = true;

        var m = PoseMetrics.Compute(pose);
        PoseVerdict v;
        if (!m.HasPerson) v = PoseVerdict.Fail(NoPersonReason);
        else
        {
            v = _checker.Evaluate(pose, m, dt);
            if (v.InPose && _held[v.Side] >= _perSide - 1e-4f)
                v = PoseVerdict.Fail(SwitchSidesReason);
        }

        if (v.InPose)
        {
            _lost = 0;
            _settled += dt;
            if (_holding || _settled >= (float)SettleTime.TotalSeconds)
            {
                _holding = true;
                _held[v.Side] = Math.Min(_perSide, _held[v.Side] + dt);
                State = StretchState.Holding;
            }
            else State = StretchState.InPose;
            Reason = null;
        }
        else
        {
            _lost += dt;
            Reason = v.Reason;
            if (_holding && _lost <= (float)GraceTime.TotalSeconds)
            {
                State = StretchState.Holding; // brief jitter: freeze progress, keep state
            }
            else
            {
                _holding = false;
                _settled = 0;
                State = m.HasPerson ? StretchState.GetReady : StretchState.NoPerson;
                for (int i = 0; i < _checker.Sides; i++)
                    if (_held[i] < _perSide - 1e-4f)
                        _held[i] = Math.Max(0f, _held[i] - dt * DecayRate);
            }
        }

        float total = 0;
        bool all = true;
        for (int i = 0; i < _checker.Sides; i++)
        {
            total += _held[i];
            if (_held[i] < _perSide - 1e-4f) all = false;
        }
        Progress = all ? 1f : Math.Clamp(total / (float)HoldTarget.TotalSeconds, 0f, 1f);
        if (all)
        {
            State = StretchState.Done;
            Reason = null;
        }
        return State;
    }
}
