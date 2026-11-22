namespace DeskPal.Pose;

/// <summary>What the once-a-second watchdog of a running PoseSession decides.</summary>
internal enum WatchdogVerdict
{
    KeepGoing,
    TimedOut,
    CameraLost,
}

/// <summary>
/// The time rules of a camera run. Every limit counts from the moment the camera is on (the start reported
/// Started), never from the click: Windows may first ask the user for camera access, and
/// however long that takes must not count as a camera that sends nothing.
/// </summary>
internal static class PoseWatchdog
{
    public static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FrameGapTimeout = TimeSpan.FromSeconds(5);

    /// <param name="sinceCameraOn">Time since the camera start finished.</param>
    /// <param name="sinceLastResult">Time since the last frame went through the model, or null before the first one.</param>
    /// <param name="maxRunTime">The safety limit for one run.</param>
    public static WatchdogVerdict Check(TimeSpan sinceCameraOn, TimeSpan? sinceLastResult, TimeSpan maxRunTime)
    {
        if (sinceCameraOn >= maxRunTime) return WatchdogVerdict.TimedOut;
        if (sinceLastResult is { } gap ? gap > FrameGapTimeout : sinceCameraOn > FirstFrameTimeout)
            return WatchdogVerdict.CameraLost;
        return WatchdogVerdict.KeepGoing;
    }
}
