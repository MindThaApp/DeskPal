using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskPal.Core.Pose;
using Microsoft.Windows.System.Power;
using Windows.Graphics.Imaging;

namespace DeskPal.Pose;

/// <summary>What <see cref="PoseSession.StartAsync"/> found.</summary>
public enum PoseStartResult
{
    /// <summary>The camera is on and pose frames are coming.</summary>
    Started,
    /// <summary>No video camera is connected.</summary>
    NoCamera,
    /// <summary>Another app is using the camera.</summary>
    CameraInUse,
    /// <summary>Windows privacy settings block camera access (link to <see cref="PoseSession.CameraPrivacySettings"/>).</summary>
    CameraBlocked,
    /// <summary>The pose model or its runtime could not load.</summary>
    ModelUnavailable,
    /// <summary>The camera could not start for another reason.</summary>
    Failed,
}

/// <summary>Why a running session ended. The camera is already released when this is reported.</summary>
public enum PoseStopReason
{
    /// <summary>StopAsync or DisposeAsync was called.</summary>
    Requested,
    /// <summary>The safety limit (<see cref="PoseSession.MaxRunTime"/>) ran out.</summary>
    TimedOut,
    /// <summary>The camera was unplugged, failed, or stopped sending frames.</summary>
    CameraLost,
    /// <summary>Windows blocked the camera while it was running.</summary>
    CameraBlocked,
    /// <summary>The PC was locked.</summary>
    PcLocked,
    /// <summary>The PC is going to sleep.</summary>
    PcSleeping,
    /// <summary>Desk Pal is closing.</summary>
    AppExit,
    /// <summary>The pose model failed on a frame.</summary>
    Error,
}

/// <summary>
/// One "Stretch with Bloop" camera check. Opens the camera, finds the body points on the PC and reports them as
/// <see cref="PoseFrame"/>s (points only, no image data). Video is processed in memory, frame by frame, and is never
/// recorded, saved or sent. Nothing runs before <see cref="StartAsync"/> or after the session stops. Start, stop and
/// dispose are safe from any thread; stopping twice is fine.
/// </summary>
public sealed class PoseSession : IAsyncDisposable
{
    public static readonly Uri CameraPrivacySettings = new("ms-settings:privacy-webcam");
    public static readonly TimeSpan DefaultMaxRunTime = TimeSpan.FromMinutes(3);

    private static readonly object ActiveLock = new();
    private static readonly HashSet<PoseSession> Active = [];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PoseImage _image = new();
    private readonly long _minFrameTicks;
    private PoseCamera? _camera;
    private PoseModel? _model;
    private PoseModel? _orphan;  // a stopped run's model, left for the frame still inside it to free
    private Timer? _watchdog;
    private Stopwatch _clock = new();
    private int _running;      // 1 while frames are wanted
    private int _busy;         // 1 while a frame is being processed
    private int _stopQueued;   // 1 once a stop from inside has been queued for this run
    private int _run;          // counts runs, so a stop queued for an old run never ends a new one
    private long _lastFrameTicks;
    private long _lastResultTicks;
    private long _cameraOnTicks; // when the camera start finished; the watchdog's limits count from here
    private bool _disposed;

    // Stats for the current or last run (read with Stats).
    private int _frames, _dropped;
    private double _inferenceMs;

    public PoseSession(TimeSpan? maxRunTime = null, double targetFps = 15)
    {
        MaxRunTime = maxRunTime ?? DefaultMaxRunTime;
        _minFrameTicks = (long)(Stopwatch.Frequency / Math.Clamp(targetFps, 1, 30) * 0.9);
    }

    /// <summary>The camera always turns off after this long, whatever the caller does.</summary>
    public TimeSpan MaxRunTime { get; }

    /// <summary>A new pose. Raised on a background thread; marshal to the UI thread before touching UI.</summary>
    public event EventHandler<PoseFrame>? FrameReady;

    /// <summary>The session ended and the camera is released. Raised once per run, on a background thread.</summary>
    public event EventHandler<PoseStopReason>? Stopped;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>A short camera format note for diagnostics (size, format, frame rate). No image data.</summary>
    public string? CameraFormat => _camera?.FormatDescription;

    public PoseStats Stats
    {
        get
        {
            var seconds = _clock.Elapsed.TotalSeconds;
            int frames = Volatile.Read(ref _frames);
            return new PoseStats(frames, Volatile.Read(ref _dropped), seconds > 0 ? frames / seconds : 0, frames > 0 ? _inferenceMs / frames : 0);
        }
    }

    public async Task<PoseStartResult> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return PoseStartResult.Started;

            // Off the caller's thread: model load and camera start both take a moment.
            return await Task.Run(() => StartCoreAsync(ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<PoseStartResult> StartCoreAsync(CancellationToken ct)
    {
        try { _model = PoseModel.Load(); }
        catch (Exception ex)
        {
            Logger.Log("pose model load failed: " + ex.GetType().Name);
            return PoseStartResult.ModelUnavailable;
        }
        // The card may have closed while the model loaded: free it and never touch the camera.
        if (ct.IsCancellationRequested)
        {
            await ReleaseAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        var camera = new PoseCamera();
        camera.FrameArrived += OnFrame;
        camera.Lost += reason => QueueStop(reason);
        _camera = camera;
        _frames = _dropped = 0;
        _inferenceMs = 0;
        _lastFrameTicks = 0;
        _lastResultTicks = 0;
        _cameraOnTicks = 0;
        _stopQueued = 0;
        Interlocked.Increment(ref _run);
        _clock = Stopwatch.StartNew();
        Volatile.Write(ref _running, 1);

        PoseStartResult result;
        try { result = await camera.StartAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { await ReleaseAsync().ConfigureAwait(false); throw; }
        catch (Exception ex)
        {
            Logger.Log($"pose camera start failed: 0x{ex.HResult:X8}");
            result = PoseStartResult.Failed;
        }
        if (result != PoseStartResult.Started)
        {
            await ReleaseAsync().ConfigureAwait(false);
            return result;
        }
        // Stopped while the start was finishing (for example the card closed during Windows' camera prompt).
        if (ct.IsCancellationRequested)
        {
            await ReleaseAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        // The watchdog's limits count from here, not from the click: the start can include Windows' camera prompt.
        Volatile.Write(ref _cameraOnTicks, Stopwatch.GetTimestamp());
        lock (ActiveLock) Active.Add(this);
        try { PowerManager.SystemSuspendStatusChanged += OnSuspendChanged; } catch { }
        _watchdog = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return PoseStartResult.Started;
    }

    /// <summary>Turns the camera off and frees the model. Safe to call at any time and more than once.</summary>
    public Task StopAsync() => StopAsync(PoseStopReason.Requested, null);

    private async Task StopAsync(PoseStopReason reason, int? run)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        bool wasRunning;
        try
        {
            wasRunning = IsRunning && (run is null || run == Volatile.Read(ref _run));
            if (!wasRunning) return;
            await ReleaseAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Logger.Log($"pose session stopped: {reason}");
        try { Stopped?.Invoke(this, reason); } catch (Exception ex) { Logger.Log("pose stopped handler failed: " + ex.Message); }
    }

    /// <summary>Called with the gate held (or before the run became visible).</summary>
    private async Task ReleaseAsync()
    {
        Volatile.Write(ref _running, 0);
        lock (ActiveLock) Active.Remove(this);
        try { PowerManager.SystemSuspendStatusChanged -= OnSuspendChanged; } catch { }
        var watchdog = Interlocked.Exchange(ref _watchdog, null);
        if (watchdog is not null) await watchdog.DisposeAsync().ConfigureAwait(false);

        var camera = Interlocked.Exchange(ref _camera, null);
        if (camera is not null)
        {
            camera.FrameArrived -= OnFrame;
            await camera.StopAsync().ConfigureAwait(false);
        }
        // A frame already inside the model finishes before the model goes away.
        for (int i = 0; i < 200 && Volatile.Read(ref _busy) != 0; i++) await Task.Delay(5).ConfigureAwait(false);
        var model = Interlocked.Exchange(ref _model, null);
        if (Volatile.Read(ref _busy) == 0)
        {
            model?.Dispose();
            _image.Clear();
        }
        else if (model is not null)
        {
            // Still busy after a second (never seen): the frame frees the model and image when it ends. The exchange
            // decides who frees it if the frame ended in between.
            Logger.Log("pose model still busy at stop");
            Volatile.Write(ref _orphan, model);
            if (Volatile.Read(ref _busy) == 0 && Interlocked.Exchange(ref _orphan, null) is { } orphan)
            {
                orphan.Dispose();
                _image.Clear();
            }
        }
        _clock.Stop();
    }

    private void QueueStop(PoseStopReason reason)
    {
        if (!IsRunning || Interlocked.Exchange(ref _stopQueued, 1) == 1) return;
        int run = Volatile.Read(ref _run);
        _ = Task.Run(() => StopAsync(reason, run)).ContinueWith(
            t => Logger.Log("pose stop failed: " + (t.Exception?.GetBaseException().GetType().Name ?? "unknown")),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void OnFrame(SoftwareBitmap bitmap)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _lastFrameTicks) < _minFrameTicks) return; // keep to about 15 a second
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _dropped); return; } // never queue
        int run = Volatile.Read(ref _run);
        try
        {
            var model = _model;
            if (!IsRunning || model is null) return;
            Volatile.Write(ref _lastFrameTicks, now);
            _image.CopyFrom(bitmap);
            long start = Stopwatch.GetTimestamp();
            var pose = model.Run(_image.Pixels, _image.Width, _image.Height, _image.Stride, _clock.Elapsed);
            _inferenceMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Interlocked.Increment(ref _frames);
            Volatile.Write(ref _lastResultTicks, Stopwatch.GetTimestamp());
            // The session may have stopped while the model ran: no pose reaches the caller after Stopped.
            if (!IsRunning || run != Volatile.Read(ref _run)) return;
            FrameReady?.Invoke(this, pose);
        }
        catch (Exception ex)
        {
            Logger.Log("pose frame failed: " + ex.GetType().Name + " " + ex.Message);
            QueueStop(PoseStopReason.Error);
        }
        finally
        {
            // A stop that timed out waiting for this frame left the model here to free; done before _busy clears so
            // a new run's first frame cannot be using _image yet.
            if (Interlocked.Exchange(ref _orphan, null) is { } orphan)
            {
                try { orphan.Dispose(); } catch (Exception ex) { Logger.Log("pose model dispose failed: " + ex.GetType().Name); }
                _image.Clear();
            }
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Watch()
    {
        if (!IsRunning) return;
        long on = Volatile.Read(ref _cameraOnTicks);
        if (on == 0) return;
        long last = Volatile.Read(ref _lastResultTicks);
        var verdict = PoseWatchdog.Check(Stopwatch.GetElapsedTime(on), last == 0 ? null : Stopwatch.GetElapsedTime(last), MaxRunTime);
        if (verdict == WatchdogVerdict.TimedOut) { QueueStop(PoseStopReason.TimedOut); return; }
        if (IsPcLocked()) { QueueStop(PoseStopReason.PcLocked); return; }
        if (verdict == WatchdogVerdict.CameraLost) QueueStop(PoseStopReason.CameraLost);
    }

    private void OnSuspendChanged(object? sender, object e)
    {
        try { if (PowerManager.SystemSuspendStatus == SystemSuspendStatus.Entering) QueueStop(PoseStopReason.PcSleeping); }
        catch { }
    }

    /// <summary>The input desktop cannot be opened while the lock screen is up.</summary>
    private static bool IsPcLocked()
    {
        var desk = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desk == IntPtr.Zero) return true;
        CloseDesktop(desk);
        return false;
    }

    /// <summary>Stops every running session (app exit). Waits briefly; the OS frees the camera at process end anyway.</summary>
    public static void StopAllForExit()
    {
        PoseSession[] sessions;
        lock (ActiveLock) sessions = [.. Active];
        if (sessions.Length == 0) return;
        try { Task.Run(() => Task.WhenAll(sessions.Select(s => s.StopAsync(PoseStopReason.AppExit, null)))).Wait(TimeSpan.FromSeconds(2)); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; }
        finally { _gate.Release(); }
    }

    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}

/// <summary>Counts for one run: frames processed, frames dropped because the model was busy, frames per second and mean model time.</summary>
public readonly record struct PoseStats(int Frames, int Dropped, double Fps, double MeanInferenceMs);
