namespace DeskPal.Core;

public interface ICallDetector
{
    bool IsInCall();
}

// Turns "microphone or camera in use" into a seated "call" hold that must be renewed on every poll.
public sealed class CallMonitor(ICallDetector detector, NudgeScheduler scheduler, TimeProvider time)
{
    public static readonly TimeSpan HoldLength = TimeSpan.FromSeconds(75);

    public void Poll()
    {
        bool inCall;
        try { inCall = detector.IsInCall(); }
        catch { inCall = false; }
        if (inCall)
            scheduler.StartHold(NudgeScheduler.CallSource, "In a call", time.GetLocalNow() + HoldLength, seated: true);
        else
            scheduler.EndHold(NudgeScheduler.CallSource);
    }
}
