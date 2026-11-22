using DeskPal.Core.Pose;

namespace DeskPal.Pose;

/// <summary>
/// --debug-pose (with --debug-main): one 10 second pose check. Logs only the start result, the camera format, the
/// top three point scores per frame, and at the end frames per second and model time. No image data is logged.
/// </summary>
internal static class PoseDebugRun
{
    private static readonly TimeSpan RunTime = TimeSpan.FromSeconds(10);

    public static void Start() => _ = Task.Run(RunAsync);

    private static async Task RunAsync()
    {
        try
        {
            await using var session = new PoseSession(maxRunTime: RunTime + TimeSpan.FromSeconds(5));
            var stopped = new TaskCompletionSource<PoseStopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.FrameReady += (_, frame) => Logger.Log("debug pose frame " + Top3(frame));
            session.Stopped += (_, reason) => stopped.TrySetResult(reason);

            var result = await session.StartAsync().ConfigureAwait(false);
            Logger.Log($"debug pose start: {result} {session.CameraFormat}");
            if (result != PoseStartResult.Started) return;

            await Task.WhenAny(stopped.Task, Task.Delay(RunTime)).ConfigureAwait(false);
            await session.StopAsync().ConfigureAwait(false);
            var stats = session.Stats;
            var reason = stopped.Task.IsCompleted ? stopped.Task.Result : PoseStopReason.Requested;
            Logger.Log($"debug pose done: {reason}, {stats.Frames} frames, {stats.Dropped} dropped, {stats.Fps:F1} fps, {stats.MeanInferenceMs:F2} ms per inference");
        }
        catch (Exception ex) { Logger.Log("debug pose failed: " + ex.GetType().Name + " " + ex.Message); }
    }

    private static string Top3(PoseFrame frame) =>
        string.Join(", ", Enumerable.Range(0, PoseFrame.PointCount)
            .OrderByDescending(i => frame.Points[i].Score)
            .Take(3)
            .Select(i => $"{(BodyPoint)i}={frame.Points[i].Score:F2}"));
}
