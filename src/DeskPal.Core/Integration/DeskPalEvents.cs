using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskPal.Core.Integration;

// The fixed event set Desk Pal can share with Automation Portal. See docs/DESKPAL-PIPE-CONTRACT.md.
public static class DeskPalEventNames
{
    public const string BreakStarted = "deskpal.break.started";
    public const string BreakDone = "deskpal.break.done";
    public const string BreakSkipped = "deskpal.break.skipped";
    public const string FocusOn = "deskpal.focus.on";
    public const string FocusOff = "deskpal.focus.off";
    public const string PauseOn = "deskpal.pause.on";
    public const string PauseOff = "deskpal.pause.off";
    public const string GoalReached = "deskpal.goal.reached";
    public const string DaySummary = "deskpal.day.summary";

    public static IReadOnlyList<string> All { get; } =
        [BreakStarted, BreakDone, BreakSkipped, FocusOn, FocusOff, PauseOn, PauseOff, GoalReached, DaySummary];
}

public enum EventCause { User, External, Schedule }

// One event. Data holds only the event's own fields (wellbeing counts and times, never titles or app names).
public sealed record DeskPalEvent(string Name, DateTimeOffset At, EventCause Cause, JsonObject Data)
{
    public static string FormatTime(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string CauseText(EventCause cause) => cause switch
    {
        EventCause.External => "external",
        EventCause.Schedule => "schedule",
        _ => "user",
    };

    // {"event":"deskpal.focus.on","at":"2026-11-12T10:00:00+01:00","cause":"user","minutes":25}
    public string ToJson()
    {
        var o = new JsonObject
        {
            ["event"] = Name,
            ["at"] = FormatTime(At),
            ["cause"] = CauseText(Cause),
        };
        foreach (var (key, value) in Data) o[key] = value?.DeepClone();
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

// Loop guard and throttle for events leaving the app: externally caused events never go out, and each name goes out at most once a minute.
public sealed class EventGate(TimeProvider time)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, DateTimeOffset> _last = [];
    private readonly object _lock = new();

    public bool Allow(DeskPalEvent e)
    {
        if (e.Cause == EventCause.External) return false;
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_last.TryGetValue(e.Name, out var last) && now - last < MinInterval) return false;
            _last[e.Name] = now;
            return true;
        }
    }
}

// Turns scheduler events into Desk Pal events. Subscribes to the scheduler directly, so it sees each event on the thread
// (and inside the scope) that caused it.
public sealed class DeskPalEventHub
{
    [ThreadStatic] private static int t_externalDepth;

    private readonly NudgeScheduler _scheduler;
    private readonly DeskPalSettings _settings;
    private readonly TimeProvider _time;
    private readonly EventGate _gate;
    private readonly object _lock = new();
    private readonly HashSet<string> _goalsReached = [];
    private DateOnly _goalsDate;
    private DateOnly _summaryDate;
    private bool? _wasWorking;

    public DeskPalEventHub(NudgeScheduler scheduler, DeskPalSettings settings, TimeProvider time)
    {
        _scheduler = scheduler;
        _settings = settings;
        _time = time;
        _gate = new EventGate(time);
        scheduler.Event += OnSchedulerEvent;
    }

    // Every event, whatever caused it.
    public event Action<DeskPalEvent>? Raised;

    // Events that pass the loop guard and the throttle: the ones that may be sent to other apps.
    public event Action<DeskPalEvent>? Published;

    // Everything the scheduler raises inside this scope is tagged cause "external" (a command from another app).
    public static IDisposable External()
    {
        t_externalDepth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            t_externalDepth--;
        }
    }

    private static EventCause CauseOr(EventCause fallback) => t_externalDepth > 0 ? EventCause.External : fallback;

    // Call on every scheduler tick: raises the day summary once, when the working day ends.
    public void Tick()
    {
        var now = _time.GetLocalNow();
        var working = _scheduler.IsWorkingTime(now);
        bool summary;
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(now.Date);
            summary = _wasWorking == true && !working && _summaryDate != today;
            if (summary) _summaryDate = today;
            _wasWorking = working;
        }
        if (!summary) return;
        var counts = new JsonObject
        {
            ["stretches"] = _scheduler.StretchesToday,
            ["eyeBreaks"] = _scheduler.EyeBreaksToday,
            ["walks"] = _scheduler.WalksToday,
        };
        Emit(DeskPalEventNames.DaySummary, now, CauseOr(EventCause.Schedule), new JsonObject { ["counts"] = counts });
    }

    private void OnSchedulerEvent(SchedulerEvent e)
    {
        var now = _time.GetLocalNow();
        switch (e.Type)
        {
            case SchedulerEventType.NudgeShown when e.Nudge is { IsTest: false } n && KindText(n.Primary) is { } kind:
                Emit(DeskPalEventNames.BreakStarted, now, CauseOr(EventCause.Schedule), new JsonObject { ["kind"] = kind });
                break;
            case SchedulerEventType.EyeCue:
                Emit(DeskPalEventNames.BreakStarted, now, CauseOr(EventCause.Schedule), new JsonObject { ["kind"] = "eyes" });
                CheckGoals(now, CauseOr(EventCause.Schedule));
                break;
            case SchedulerEventType.NudgeDone when e.Nudge is { IsTest: false } n && KindText(n.Primary) is { } kind:
            {
                var data = new JsonObject { ["kind"] = kind };
                var minutes = (int)Math.Round((now - n.ShownAt).TotalMinutes);
                if (minutes >= 1) data["minutes"] = minutes;
                Emit(DeskPalEventNames.BreakDone, now, CauseOr(EventCause.User), data);
                CheckGoals(now, CauseOr(EventCause.User));
                break;
            }
            case SchedulerEventType.NudgeSkipped when e.Nudge is { IsTest: false } n && KindText(n.Primary) is { } kind:
                Emit(DeskPalEventNames.BreakSkipped, now, CauseOr(EventCause.User), new JsonObject { ["kind"] = kind });
                break;
            case SchedulerEventType.HoldStarted when e.Hold is { } h:
                if (h.Source == NudgeScheduler.ManualSource)
                    Emit(DeskPalEventNames.PauseOn, now, CauseOr(EventCause.User), new JsonObject { ["until"] = DeskPalEvent.FormatTime(h.Until) });
                else if (h.Source == NudgeScheduler.FocusSource)
                    Emit(DeskPalEventNames.FocusOn, now, CauseOr(EventCause.User),
                        new JsonObject { ["minutes"] = Math.Max(1, (int)Math.Round((h.Until - now).TotalMinutes)) });
                break;
            case SchedulerEventType.HoldEnded when e.Hold is { } h:
            {
                // A hold that ran out ended by itself; one released early was stopped by someone.
                var expired = h.Until <= now;
                if (h.Source == NudgeScheduler.ManualSource)
                    Emit(DeskPalEventNames.PauseOff, now, CauseOr(expired ? EventCause.Schedule : EventCause.User), new JsonObject());
                else if (h.Source == NudgeScheduler.FocusSource)
                    Emit(DeskPalEventNames.FocusOff, now, CauseOr(expired ? EventCause.Schedule : EventCause.User),
                        new JsonObject { ["reason"] = expired ? "ended" : "stopped" });
                break;
            }
        }
    }

    private void CheckGoals(DateTimeOffset now, EventCause cause)
    {
        var goals = DailyGoals.For(_settings);
        var reached = new List<string>();
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(now.Date);
            if (_goalsDate != today)
            {
                _goalsDate = today;
                _goalsReached.Clear();
            }
            // Equal, not at least: the counters move one at a time, and a restart with a goal already met stays quiet.
            if (goals.Stretches > 0 && _scheduler.StretchesToday == goals.Stretches && _goalsReached.Add("stretches")) reached.Add("stretches");
            if (goals.EyeBreaks > 0 && _scheduler.EyeBreaksToday == goals.EyeBreaks && _goalsReached.Add("eyeBreaks")) reached.Add("eyeBreaks");
            if (goals.Walks > 0 && _scheduler.WalksToday == goals.Walks && _goalsReached.Add("walks")) reached.Add("walks");
        }
        foreach (var goal in reached)
            Emit(DeskPalEventNames.GoalReached, now, cause, new JsonObject { ["goal"] = goal });
    }

    public static string? KindText(NudgeKind kind) => kind switch
    {
        NudgeKind.Stretch => "stretch",
        NudgeKind.Water => "water",
        NudgeKind.Eyes => "eyes",
        NudgeKind.Walk => "walk",
        _ => null,
    };

    private void Emit(string name, DateTimeOffset at, EventCause cause, JsonObject data)
    {
        var e = new DeskPalEvent(name, at, cause, data);
        Raised?.Invoke(e);
        if (_gate.Allow(e)) Published?.Invoke(e);
    }
}
