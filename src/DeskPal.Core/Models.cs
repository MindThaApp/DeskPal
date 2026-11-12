namespace DeskPal.Core;

public enum NudgeKind { Stretch, Eyes, Water, Walk, Breathe }

public sealed record StretchInfo(string Id, string Name, string BodyArea, int DurationSeconds, string Instruction, string AssetName);

public sealed record Hold(string Source, string Reason, DateTimeOffset Until, bool Seated = false);

public sealed record Nudge(
    Guid Id,
    IReadOnlyList<NudgeKind> Kinds,
    NudgeKind Primary,
    string Title,
    string Message,
    string? Instruction,
    StretchInfo? Stretch,
    int? TimedSeconds,
    string AssetName,
    bool IsTest,
    DateTimeOffset ShownAt);

public enum SchedulerEventType { NudgeShown, NudgeDone, NudgeSkipped, NudgeSnoozed, HoldStarted, HoldEnded, EyeCue }

public sealed record SchedulerEvent(SchedulerEventType Type, Nudge? Nudge = null, Hold? Hold = null);

public abstract record Command;
public sealed record DoneCommand(Guid? NudgeId = null) : Command;
public sealed record SnoozeCommand(Guid? NudgeId = null) : Command;
public sealed record SkipCommand(Guid? NudgeId = null) : Command;
public sealed record TestNudgeCommand : Command;
public sealed record PauseCommand(int? Minutes = null, bool UntilTomorrow = false) : Command;
public sealed record ResumeCommand : Command;
// Shows a break of this kind now (Stretch, Water, Eyes or Walk), outside the usual timers. Refused while a nudge is showing.
public sealed record BreakNowCommand(NudgeKind Kind) : Command;
// Focus is a hold of its own source, so it ends by itself and doesn't cancel a pause.
public sealed record FocusCommand(int Minutes) : Command;
public sealed record FocusOffCommand : Command;

public interface ICommandRouter
{
    bool Execute(Command command);
}
