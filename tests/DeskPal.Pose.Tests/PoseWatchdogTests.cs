using DeskPal.Pose;

namespace DeskPal.Pose.Tests;

public class PoseWatchdogTests
{
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(3);

    [Fact]
    public void Time_spent_on_the_camera_prompt_does_not_count()
    {
        // The user took 30 s on Windows' camera prompt; the camera has been on for 2 s and no frame has come yet.
        // Only the time since the camera went on is passed in, so this is not a lost camera.
        Assert.Equal(WatchdogVerdict.KeepGoing, PoseWatchdog.Check(TimeSpan.FromSeconds(2), null, Max));
    }

    [Fact]
    public void No_first_frame_ten_seconds_after_the_camera_went_on_is_a_lost_camera()
    {
        Assert.Equal(WatchdogVerdict.KeepGoing, PoseWatchdog.Check(TimeSpan.FromSeconds(10), null, Max));
        Assert.Equal(WatchdogVerdict.CameraLost, PoseWatchdog.Check(TimeSpan.FromSeconds(10.5), null, Max));
    }

    [Fact]
    public void A_gap_in_frames_is_a_lost_camera()
    {
        Assert.Equal(WatchdogVerdict.KeepGoing, PoseWatchdog.Check(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(4), Max));
        Assert.Equal(WatchdogVerdict.CameraLost, PoseWatchdog.Check(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(6), Max));
    }

    [Fact]
    public void The_safety_limit_always_wins()
    {
        Assert.Equal(WatchdogVerdict.TimedOut, PoseWatchdog.Check(Max, TimeSpan.FromSeconds(0.1), Max));
        Assert.Equal(WatchdogVerdict.TimedOut, PoseWatchdog.Check(Max + TimeSpan.FromSeconds(1), null, Max));
    }
}
