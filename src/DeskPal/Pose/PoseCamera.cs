using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;

namespace DeskPal.Pose;

/// <summary>
/// Opens one video camera (front first) at the smallest size of at least 320 x 240, about 15 frames a second,
/// and hands each frame to <see cref="FrameArrived"/> in memory. Frames are never recorded, saved or sent.
/// <see cref="StopAsync"/> releases the camera completely (the camera light goes off).
/// </summary>
internal sealed class PoseCamera
{
    private const uint MinWidth = 320, MinHeight = 240;
    private const double TargetFps = 15;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    // Media Foundation codes that mean another app holds the camera.
    private static readonly HashSet<int> InUseCodes =
    [
        unchecked((int)0xC00D3704), // MF_E_HW_MFT_FAILED_START_STREAMING
        unchecked((int)0xC00D3EA3), // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
        unchecked((int)0x800700AA), // ERROR_BUSY
        unchecked((int)0x80070020), // ERROR_SHARING_VIOLATION
    ];

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;

    /// <summary>Raised on a camera thread. The handler must not keep the bitmap; it is disposed right after.</summary>
    public event Action<SoftwareBitmap>? FrameArrived;

    /// <summary>Raised when the camera goes away or Windows blocks it while running.</summary>
    public event Action<PoseStopReason>? Lost;

    public string? FormatDescription { get; private set; }

    public async Task<PoseStartResult> StartAsync(CancellationToken ct)
    {
        if (IsBlockedByPrivacySettings()) return PoseStartResult.CameraBlocked;

        var (group, sourceInfo) = await FindCameraAsync().ConfigureAwait(false);
        if (group is null || sourceInfo is null) return PoseStartResult.NoCamera;
        ct.ThrowIfCancellationRequested();

        var result = await OpenAsync(group, sourceInfo, MediaCaptureSharingMode.ExclusiveControl, ct).ConfigureAwait(false);
        // Another app has it: reading alongside it is still possible on many cameras.
        if (result == PoseStartResult.CameraInUse && !ct.IsCancellationRequested)
            result = await OpenAsync(group, sourceInfo, MediaCaptureSharingMode.SharedReadOnly, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<PoseStartResult> OpenAsync(MediaFrameSourceGroup group, MediaFrameSourceInfo info, MediaCaptureSharingMode mode, CancellationToken ct)
    {
        var capture = new MediaCapture();
        _capture = capture;
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = mode,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            }).AsTask(ct).ConfigureAwait(false);
            capture.Failed += OnCaptureFailed;
            capture.CameraStreamStateChanged += OnStreamStateChanged;

            if (!capture.FrameSources.TryGetValue(info.Id, out var source)) { await StopAsync().ConfigureAwait(false); return PoseStartResult.Failed; }
            if (mode == MediaCaptureSharingMode.ExclusiveControl && PickFormat(source) is { } format)
                await source.SetFormatAsync(format).AsTask(ct).ConfigureAwait(false);
            var current = source.CurrentFormat;
            FormatDescription = current?.VideoFormat is { } vf
                ? $"{vf.Width}x{vf.Height} {current.Subtype} {Fps(current):F0}fps {(mode == MediaCaptureSharingMode.SharedReadOnly ? "shared" : "exclusive")}"
                : null;

            // The card may have closed while Windows asked for camera access: no frames then, and the camera never
            // turns on. The catch below releases everything.
            ct.ThrowIfCancellationRequested();
            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(ct).ConfigureAwait(false);
            _reader = reader;
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            ct.ThrowIfCancellationRequested();
            var status = await reader.StartAsync().AsTask(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (status == MediaFrameReaderStartStatus.Success) return PoseStartResult.Started;
            await StopAsync().ConfigureAwait(false);
            return status is MediaFrameReaderStartStatus.DeviceNotAvailable or MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                ? PoseStartResult.CameraInUse
                : PoseStartResult.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            await StopAsync().ConfigureAwait(false);
            return PoseStartResult.CameraBlocked;
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await StopAsync().ConfigureAwait(false);
            if (InUseCodes.Contains(ex.HResult)) return PoseStartResult.CameraInUse;
            Logger.Log($"pose camera open failed ({mode}): 0x{ex.HResult:X8} {ex.GetType().Name}");
            return PoseStartResult.Failed;
        }
    }

    /// <summary>Stops the reader and releases the camera. Safe to call more than once.</summary>
    /// <remarks>Every step is guarded on its own, so a failing step never skips the ones after it: the capture is
    /// always disposed, which is what turns the camera off.</remarks>
    public async Task StopAsync()
    {
        var reader = Interlocked.Exchange(ref _reader, null);
        var capture = Interlocked.Exchange(ref _capture, null);
        try
        {
            if (reader is not null)
            {
                try { reader.FrameArrived -= OnFrameArrived; }
                catch (Exception ex) { LogStep("reader unhook", ex); }
                // A wedged driver can leave StopAsync pending forever; the capture must still be disposed so the
                // camera turns off.
                Task? stop = null;
                try
                {
                    stop = reader.StopAsync().AsTask();
                    await stop.WaitAsync(StopTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger.Log("pose camera reader stop timed out");
                    _ = stop!.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                catch (Exception ex) { LogStep("reader stop", ex); }
                finally
                {
                    try { reader.Dispose(); }
                    catch (Exception ex) { LogStep("reader dispose", ex); }
                }
            }
        }
        finally
        {
            if (capture is not null)
            {
                try { capture.Failed -= OnCaptureFailed; }
                catch (Exception ex) { LogStep("capture unhook", ex); }
                try { capture.CameraStreamStateChanged -= OnStreamStateChanged; }
                catch (Exception ex) { LogStep("capture unhook", ex); }
                finally
                {
                    try { capture.Dispose(); }
                    catch (Exception ex) { LogStep("capture dispose", ex); }
                }
            }
        }
    }

    private static void LogStep(string step, Exception ex) => Logger.Log($"pose camera {step} failed: 0x{ex.HResult:X8} {ex.GetType().Name}");

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // An exception must never escape into the native callback: frame access throws while the reader is being torn down.
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is not null) FrameArrived?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            // Only a reader that is still current counts as a lost camera; one already handed to StopAsync is expected to fail.
            if (!ReferenceEquals(Volatile.Read(ref _reader), sender)) return;
            LogStep("frame", ex);
            Lost?.Invoke(PoseStopReason.CameraLost);
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        Logger.Log($"pose camera failed: 0x{args.Code:X8}");
        Lost?.Invoke(PoseStopReason.CameraLost);
    }

    private void OnStreamStateChanged(MediaCapture sender, object args)
    {
        CameraStreamState state;
        try { state = sender.CameraStreamState; } catch { return; }
        if (state == CameraStreamState.BlockedForPrivacy) Lost?.Invoke(PoseStopReason.CameraBlocked);
        else if (state == CameraStreamState.Shutdown) Lost?.Invoke(PoseStopReason.CameraLost);
    }

    private static async Task<(MediaFrameSourceGroup?, MediaFrameSourceInfo?)> FindCameraAsync()
    {
        IReadOnlyList<MediaFrameSourceGroup> groups;
        try { groups = await MediaFrameSourceGroup.FindAllAsync().AsTask().ConfigureAwait(false); }
        catch { return (null, null); }
        MediaFrameSourceGroup? bestGroup = null;
        MediaFrameSourceInfo? best = null;
        int bestRank = int.MaxValue;
        foreach (var group in groups)
        {
            foreach (var info in group.SourceInfos)
            {
                if (info.SourceKind != MediaFrameSourceKind.Color) continue;
                int rank = info.MediaStreamType switch
                {
                    Windows.Media.Capture.MediaStreamType.VideoPreview => 0,
                    Windows.Media.Capture.MediaStreamType.VideoRecord => 1,
                    _ => -1,
                };
                if (rank < 0) continue;
                // Front-facing cameras first.
                if (info.DeviceInformation?.EnclosureLocation?.Panel != Panel.Front) rank += 2;
                if (rank < bestRank) { bestRank = rank; best = info; bestGroup = group; }
            }
        }
        return (bestGroup, best);
    }

    /// <summary>The smallest format of at least 320 x 240, with the frame rate closest to 15 (at least 15 if offered).</summary>
    private static MediaFrameFormat? PickFormat(MediaFrameSource source) =>
        source.SupportedFormats
            .Where(f => f.VideoFormat is { } v && v.Width >= MinWidth && v.Height >= MinHeight)
            .OrderBy(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenBy(f => Fps(f) >= TargetFps ? Fps(f) - TargetFps : 1000 + TargetFps - Fps(f))
            // Uncompressed formats convert to BGRA more cheaply than MJPG.
            .ThenBy(f => string.Equals(f.Subtype, MediaEncodingSubtypes.Mjpg, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    private static double Fps(MediaFrameFormat f) =>
        f.FrameRate is { Denominator: > 0 } r ? (double)r.Numerator / r.Denominator : 0;

    private static bool IsBlockedByPrivacySettings()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                return false;
            var status = Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("webcam").CheckAccess();
            return status is Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.DeniedByUser
                or Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.DeniedBySystem;
        }
        catch { return false; }
    }
}
