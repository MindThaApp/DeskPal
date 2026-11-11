namespace DeskPal.Core;

public sealed class NudgeScheduler : ICommandRouter
{
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(5);
    public const int WalkAfterStretches = 3;
    public const string SnoozeSource = "snooze";
    public const string ManualSource = "manual";

    private static readonly NudgeKind[] Scheduled = [NudgeKind.Stretch, NudgeKind.Eyes, NudgeKind.Water];

    private readonly TimeProvider _time;
    private readonly DeskPalSettings _settings;
    private readonly StretchLibrary _library;
    private readonly object _gate = new();
    private readonly Dictionary<NudgeKind, DateTimeOffset> _due = [];
    private readonly Dictionary<NudgeKind, DateTimeOffset> _pending = [];
    private readonly List<Hold> _holds = [];
    private DateTimeOffset? _lastShown;

    public NudgeScheduler(TimeProvider time, DeskPalSettings settings, DeskPalState? state = null)
    {
        _time = time;
        _settings = settings;
        _library = new StretchLibrary(state?.LastStretchId);
        StretchesCompleted = state?.StretchesCompleted ?? 0;
        var now = Now;
        foreach (var kind in Scheduled) _due[kind] = now + settings.IntervalFor(kind);
        if (state is not null)
            foreach (var h in state.Holds)
                if (h.Until > now && h.Source != "dnd") _holds.Add(h);
    }

    public event Action<SchedulerEvent>? Event;

    public Nudge? Current { get; private set; }
    public int StretchesCompleted { get; private set; }

    private DateTimeOffset Now => _time.GetLocalNow();

    public IReadOnlyList<Hold> Holds
    {
        get { lock (_gate) return _holds.ToList(); }
    }

    public Hold? ManualHold
    {
        get { lock (_gate) return _holds.FirstOrDefault(h => h.Source == ManualSource); }
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public (NudgeKind Kind, DateTimeOffset At)? NextDue
    {
        get
        {
            lock (_gate)
            {
                if (_due.Count == 0) return null;
                var best = _due.OrderBy(p => p.Value).First();
                var kind = best.Key == NudgeKind.Stretch && StretchesCompleted >= WalkAfterStretches ? NudgeKind.Walk : best.Key;
                return (kind, best.Value);
            }
        }
    }

    public DeskPalState Snapshot()
    {
        lock (_gate)
            return new DeskPalState
            {
                Holds = _holds.Where(h => h.Source != "dnd").ToList(),
                StretchesCompleted = StretchesCompleted,
                LastStretchId = _library.LastId,
            };
    }

    public bool IsWorkingTime(DateTimeOffset at)
    {
        var wh = _settings.WorkingHours;
        var t = TimeOnly.FromTimeSpan(at.TimeOfDay);
        return Array.IndexOf(wh.Days, at.DayOfWeek) >= 0 && t >= wh.Start && t < wh.End;
    }

    public void StartHold(string source, string reason, DateTimeOffset until, bool seated = false)
    {
        lock (_gate)
        {
            var hold = new Hold(source, reason, until, seated);
            var index = _holds.FindIndex(h => h.Source == source);
            if (index >= 0)
            {
                _holds[index] = hold;
                return;
            }
            _holds.Add(hold);
            Raise(new SchedulerEvent(SchedulerEventType.HoldStarted, Hold: hold));
        }
    }

    public bool EndHold(string source)
    {
        lock (_gate)
        {
            var index = _holds.FindIndex(h => h.Source == source);
            if (index < 0) return false;
            var hold = _holds[index];
            _holds.RemoveAt(index);
            Raise(new SchedulerEvent(SchedulerEventType.HoldEnded, Hold: hold));
        }
        Tick();
        return true;
    }

    public void Tick()
    {
        lock (_gate)
        {
            var now = Now;
            foreach (var hold in _holds.Where(h => h.Until <= now).ToList())
            {
                _holds.Remove(hold);
                Raise(new SchedulerEvent(SchedulerEventType.HoldEnded, Hold: hold));
            }

            if (Current is not null) return;

            var working = IsWorkingTime(now);
            foreach (var kind in Scheduled)
            {
                if (!_due.TryGetValue(kind, out var due) || due > now) continue;
                if (working)
                {
                    _pending[kind] = due;
                    _due.Remove(kind);
                }
                else
                {
                    _due[kind] = NextWorkStart(now) + _settings.IntervalFor(kind);
                }
            }

            if (!working)
            {
                foreach (var kind in _pending.Keys.ToList())
                    _due[kind] = NextWorkStart(now) + _settings.IntervalFor(kind);
                _pending.Clear();
                return;
            }

            if (_pending.Count == 0 || _holds.Count > 0) return;
            if (_lastShown is { } last && now - last < MinGap) return;

            var earliest = _pending.Values.Min();
            var batch = _pending.Keys.ToList();
            foreach (var (kind, due) in _due)
                if (due <= earliest + MergeWindow) batch.Add(kind);
            Show(batch, now, isTest: false);
        }
    }

    public bool Execute(Command command)
    {
        switch (command)
        {
            case DoneCommand c: return Answer(c.NudgeId, SchedulerEventType.NudgeDone);
            case SkipCommand c: return Answer(c.NudgeId, SchedulerEventType.NudgeSkipped);
            case SnoozeCommand c: return Snooze(c.NudgeId);
            case TestNudgeCommand: return RequestNudge();
            case PauseCommand c: return Pause(c);
            case ResumeCommand: return EndHold(ManualSource);
            default: return false;
        }
    }

    public bool RequestNudge()
    {
        lock (_gate)
        {
            if (Current is not null) return false;
            Show([NudgeKind.Stretch], Now, isTest: true);
            return true;
        }
    }

    // Auto-hide with no answer: counts as neither done nor skipped, so the kinds are just rescheduled.
    public void Expire(Guid nudgeId)
    {
        lock (_gate)
        {
            if (Current?.Id != nudgeId) return;
            Reschedule(Current, Now);
            Current = null;
        }
    }

    private bool Pause(PauseCommand c)
    {
        var now = Now;
        DateTimeOffset until;
        string reason;
        if (c.UntilTomorrow)
        {
            var tomorrow = now.Date.AddDays(1) + _settings.WorkingHours.Start.ToTimeSpan();
            until = new DateTimeOffset(tomorrow, _time.LocalTimeZone.GetUtcOffset(tomorrow));
            reason = "Paused until tomorrow";
        }
        else if (c.Minutes is > 0)
        {
            until = now.AddMinutes(c.Minutes.Value);
            reason = "Paused";
        }
        else return false;
        StartHold(ManualSource, reason, until);
        return true;
    }

    private bool Answer(Guid? id, SchedulerEventType type)
    {
        lock (_gate)
        {
            if (Current is null || (id is not null && id != Current.Id)) return false;
            var nudge = Current;
            var now = Now;
            if (!nudge.IsTest)
            {
                Reschedule(nudge, now);
                if (type == SchedulerEventType.NudgeDone)
                {
                    if (nudge.Kinds.Contains(NudgeKind.Walk)) StretchesCompleted = 0;
                    else if (nudge.Kinds.Contains(NudgeKind.Stretch)) StretchesCompleted++;
                }
            }
            Current = null;
            Raise(new SchedulerEvent(type, nudge));
            return true;
        }
    }

    private bool Snooze(Guid? id)
    {
        lock (_gate)
        {
            if (Current is null || (id is not null && id != Current.Id)) return false;
            var nudge = Current;
            var now = Now;
            Current = null;
            if (!nudge.IsTest)
                foreach (var kind in nudge.Kinds)
                {
                    var key = kind == NudgeKind.Walk ? NudgeKind.Stretch : kind;
                    if (Array.IndexOf(Scheduled, key) >= 0) _pending[key] = now;
                }
            _lastShown = null;
            Raise(new SchedulerEvent(SchedulerEventType.NudgeSnoozed, nudge));
            StartHold(SnoozeSource, "Snoozed", now + SnoozeDuration);
            return true;
        }
    }

    private void Reschedule(Nudge nudge, DateTimeOffset now)
    {
        if (nudge.IsTest) return;
        foreach (var kind in nudge.Kinds)
        {
            var key = kind == NudgeKind.Walk ? NudgeKind.Stretch : kind;
            if (Array.IndexOf(Scheduled, key) >= 0) _due[key] = now + _settings.IntervalFor(key);
        }
    }

    private DateTimeOffset NextWorkStart(DateTimeOffset now)
    {
        var wh = _settings.WorkingHours;
        for (var i = 0; i <= 7; i++)
        {
            var day = now.Date.AddDays(i);
            if (Array.IndexOf(wh.Days, day.DayOfWeek) < 0) continue;
            var start = day + wh.Start.ToTimeSpan();
            if (start <= now.DateTime) continue;
            return new DateTimeOffset(start, _time.LocalTimeZone.GetUtcOffset(start));
        }
        return now.AddDays(1);
    }

    private void Show(List<NudgeKind> batch, DateTimeOffset now, bool isTest)
    {
        foreach (var kind in batch)
        {
            _pending.Remove(kind);
            _due.Remove(kind);
        }

        var kinds = batch.Distinct()
            .Select(k => k == NudgeKind.Stretch && StretchesCompleted >= WalkAfterStretches ? NudgeKind.Walk : k)
            .OrderBy(k => k == NudgeKind.Walk ? -1 : (int)k)
            .ToList();
        var primary = kinds[0];
        var stretch = primary == NudgeKind.Stretch ? _library.Next() : null;
        var (title, instruction) = Describe(primary, stretch);
        string message;
        string? extra = null;
        if (kinds.Count == 1)
        {
            message = instruction;
        }
        else
        {
            message = $"{title}, {string.Join(" and ", kinds.Skip(1).Select(Tail))}.";
            extra = instruction;
        }

        int? timed = primary switch
        {
            NudgeKind.Stretch => stretch!.DurationSeconds,
            NudgeKind.Eyes => 20,
            NudgeKind.Breathe => 60,
            _ => null,
        };
        var asset = primary switch
        {
            NudgeKind.Stretch => stretch!.AssetName,
            NudgeKind.Walk => "bloop-walk",
            _ => "bloop-idle",
        };

        var nudge = new Nudge(Guid.NewGuid(), kinds, primary, title, message, extra, stretch, timed, asset, isTest, now);
        Current = nudge;
        if (!isTest) _lastShown = now;
        Raise(new SchedulerEvent(SchedulerEventType.NudgeShown, nudge));
    }

    private static (string Title, string Instruction) Describe(NudgeKind kind, StretchInfo? stretch) => kind switch
    {
        NudgeKind.Stretch => ("Stretch time", stretch!.Name + ". " + stretch.Instruction),
        NudgeKind.Walk => ("Walk break", "Stand up and take a short walk, even just to the next room."),
        NudgeKind.Eyes => ("Eyes break", "Look at something about 20 feet away for 20 seconds."),
        NudgeKind.Water => ("Water time", "Grab a glass of water."),
        _ => ("Breathe", "Take a few slow, deep breaths."),
    };

    private static string Tail(NudgeKind kind) => kind switch
    {
        NudgeKind.Stretch => "give your body a stretch",
        NudgeKind.Walk => "take a short walk",
        NudgeKind.Eyes => "rest your eyes for 20 seconds",
        NudgeKind.Water => "grab some water while you're up",
        _ => "take a few slow breaths",
    };

    private void Raise(SchedulerEvent e) => Event?.Invoke(e);
}

