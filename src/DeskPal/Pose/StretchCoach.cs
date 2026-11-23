using DeskPal.Core.Pose;
using Microsoft.UI.Dispatching;

namespace DeskPal.Pose;

/// <summary>What the card shows for one moment of a camera stretch.</summary>
internal sealed record CoachSnapshot(StretchState State, float Progress, string? Reason, float LeftSide, float RightSide, int Sides, MirrorValues Mirror);

/// <summary>
/// One "Stretch with Bloop" run for the reminder card: a camera session (or, with --debug-pose-card, a scripted frame
/// sequence that never touches the camera) feeding a stretch check and Bloop's mirror values. Frames arrive on a
/// background thread; the card hears about them on the UI thread, at most one update waiting at a time. Nothing is
/// stored: only body points pass through here.
/// </summary>
internal sealed class StretchCoach
{
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(5);
    private static readonly object ReleaseLock = new();
    private static Task _releasing = Task.CompletedTask;

    private readonly DispatcherQueue _ui;
    private readonly string _stretchId;
    private readonly int _stretchSeconds;
    private readonly bool _synthetic;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private PoseSession? _session;
    private StretchCheck? _check;
    private MirrorPose? _mirror;
    private CoachSnapshot? _latest;
    private int _posted;
    private volatile bool _stopped;
    private int _ended;

    public StretchCoach(DispatcherQueue ui, string stretchId, int stretchSeconds, bool synthetic)
    {
        _ui = ui;
        _stretchId = stretchId;
        _stretchSeconds = stretchSeconds;
        _synthetic = synthetic;
    }

    /// <summary>
    /// Stretch with Bloop is offered only in the packaged (Store) build: that is where the camera permission and the
    /// C++ runtime the pose model needs come from. The portable build never shows the button. --debug-pose-card
    /// counts too, since it only plays scripted frames and never touches the camera.
    /// </summary>
    public static bool Available => PackagedApp.IsPackaged || AppData.DebugPoseCard;

    public bool Stopped => _stopped;

    /// <summary>On the UI thread, after a frame was judged.</summary>
    public event Action<CoachSnapshot>? Updated;

    /// <summary>On the UI thread, when a running session ended by itself (not when Stop was called).</summary>
    public event Action<PoseStopReason>? Ended;

    public async Task<PoseStartResult> StartAsync()
    {
        if (_synthetic)
        {
            Begin(1f);
            _ = Task.Run(() => RunScript(_cts.Token));
            return PoseStartResult.Started;
        }

        // A camera from the last run may still be closing in the background; opening it again before that finished
        // would report "camera in use". The wait is short (a release takes well under a second).
        Task releasing;
        lock (ReleaseLock) releasing = _releasing;
        try { await releasing.WaitAsync(ReleaseWait, _cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return PoseStartResult.Failed; }
        catch (TimeoutException) { Logger.Log("stretch camera still closing; starting anyway"); }
        if (_stopped) return PoseStartResult.Failed;

        var session = new PoseSession();
        _session = session;
        session.FrameReady += (_, frame) => OnFrame(frame);
        session.Stopped += (_, reason) => OnSessionStopped(reason);
        PoseStartResult result;
        try { result = await session.StartAsync(_cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { result = PoseStartResult.Failed; }
        catch (Exception ex)
        {
            Logger.Log("stretch camera start failed: " + ex.GetType().Name);
            result = PoseStartResult.Failed;
        }
        if (result != PoseStartResult.Started)
        {
            ReleaseSession();
            return result;
        }
        if (_stopped)
        {
            ReleaseSession();
            return PoseStartResult.Failed;
        }
        Begin(AspectOf(session.CameraFormat));
        return result;
    }

    private void Begin(float aspect)
    {
        var check = StretchCheck.Create(_stretchId, _stretchSeconds, _synthetic ? TimeSpan.FromSeconds(4) : null, aspect)
                    ?? StretchCheck.Create("overhead-reach", _stretchSeconds, _synthetic ? TimeSpan.FromSeconds(4) : null, aspect);
        lock (_gate)
        {
            _check = check;
            _mirror = new MirrorPose(aspect);
        }
    }

    // "1280x720 NV12 30fps shared" gives 1280/720.
    private static float AspectOf(string? format)
    {
        if (format is null) return 16f / 9f;
        var first = format.Split(' ', 2)[0].Split('x');
        return first.Length == 2 && float.TryParse(first[0], out var w) && float.TryParse(first[1], out var h) && w > 0 && h > 0
            ? w / h
            : 16f / 9f;
    }

    private void OnFrame(PoseFrame frame)
    {
        if (_stopped) return;
        CoachSnapshot snapshot;
        lock (_gate)
        {
            if (_check is null || _mirror is null) return;
            _check.Update(frame);
            var m = _mirror.Update(frame);
            snapshot = new CoachSnapshot(_check.State, _check.Progress, _check.Reason, _check.SideProgress(0), _check.SideProgress(1), _check.Sides, m);
            _latest = snapshot;
        }
        if (Interlocked.Exchange(ref _posted, 1) == 0)
            _ui.TryEnqueue(() =>
            {
                Volatile.Write(ref _posted, 0);
                if (_stopped) return;
                var latest = _latest;
                if (latest is null) return;
                try { Updated?.Invoke(latest); }
                catch (Exception ex) { Logger.Log("stretch update failed: " + ex.Message); }
            });
    }

    private void OnSessionStopped(PoseStopReason reason)
    {
        if (_stopped) return;
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _stopped = true;
        _ui.TryEnqueue(() =>
        {
            try { Ended?.Invoke(reason); }
            catch (Exception ex) { Logger.Log("stretch end failed: " + ex.Message); }
        });
        ReleaseSession();
    }

    /// <summary>Stops the camera. Safe to call any number of times, from the UI thread.</summary>
    public void Stop()
    {
        _stopped = true;
        try { _cts.Cancel(); } catch { }
        ReleaseSession();
        // Body points are dropped as soon as the stretch ends.
        lock (_gate)
        {
            _check = null;
            _mirror = null;
            _latest = null;
        }
    }

    private void ReleaseSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        var release = Task.Run(async () =>
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Log("stretch camera stop failed: " + ex.GetType().Name); }
        });
        // Kept for the next start, which waits for it (shared by every card: there is one camera).
        lock (ReleaseLock) _releasing = _releasing.IsCompleted ? release : Task.WhenAll(_releasing, release);
    }

    // --- Debug script: nobody, then a person at rest, one arm up, both arms up and held (or leaning for a side bend).
    private async Task RunScript(CancellationToken ct)
    {
        var sideBend = _stretchId == "side-bend";
        var start = Environment.TickCount64;
        try
        {
            while (!ct.IsCancellationRequested && !_stopped)
            {
                var t = (Environment.TickCount64 - start) / 1000.0;
                OnFrame(sideBend ? ScriptSideBend(t) : ScriptOverhead(t));
                await Task.Delay(66, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static PoseFrame ScriptOverhead(double t)
    {
        if (t < 1.5) return Person(t, present: false);
        // 1.5 to 3.2 s arms down; 3.2 to 5 one arm; after that both arms up.
        var left = t >= 5.0 ? 1f : 0f;
        var right = t >= 3.2 ? 1f : 0f;
        return Person(t, true, left, right, 0);
    }

    private static PoseFrame ScriptSideBend(double t)
    {
        if (t < 1.5) return Person(t, present: false);
        var lean = t < 3.2 ? 0f : t < 8 ? 0.12f : -0.12f;
        return Person(t, true, 0, 0, lean);
    }

    // Camera view (not mirrored, aspect 1): the user's left side is at larger x. lean moves the shoulders and head.
    private static PoseFrame Person(double t, bool present, float leftArmUp = 0, float rightArmUp = 0, float lean = 0)
    {
        var p = new Keypoint[PoseFrame.PointCount];
        if (!present) return new PoseFrame(TimeSpan.FromSeconds(t), p);
        void Set(BodyPoint b, float x, float y) => p[(int)b] = new Keypoint(x, y, 0.95f);
        var d = -lean; // leaning toward the user's right moves the upper body to smaller x
        Set(BodyPoint.Nose, 0.5f + d, 0.30f);
        Set(BodyPoint.LeftEye, 0.53f + d, 0.28f); Set(BodyPoint.RightEye, 0.47f + d, 0.28f);
        Set(BodyPoint.LeftEar, 0.56f + d, 0.29f); Set(BodyPoint.RightEar, 0.44f + d, 0.29f);
        Set(BodyPoint.LeftShoulder, 0.62f + d, 0.50f); Set(BodyPoint.RightShoulder, 0.38f + d, 0.50f);
        Set(BodyPoint.LeftElbow, 0.64f + d, Lerp(0.65f, 0.34f, leftArmUp)); Set(BodyPoint.RightElbow, 0.36f + d, Lerp(0.65f, 0.34f, rightArmUp));
        Set(BodyPoint.LeftWrist, Lerp(0.64f, 0.60f, leftArmUp) + d, Lerp(0.78f, 0.12f, leftArmUp));
        Set(BodyPoint.RightWrist, Lerp(0.36f, 0.40f, rightArmUp) + d, Lerp(0.78f, 0.12f, rightArmUp));
        Set(BodyPoint.LeftHip, 0.58f, 0.90f); Set(BodyPoint.RightHip, 0.42f, 0.90f);
        return new PoseFrame(TimeSpan.FromSeconds(t), p);
    }

    private static float Lerp(float a, float b, float k) => a + (b - a) * k;
}
