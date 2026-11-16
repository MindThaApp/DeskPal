namespace DeskPal.Core;

public sealed class NudgeScheduler : ICommandRouter
{
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LongCall = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);
    public const int MaxVisiblePerHour = 2;
    public const string SnoozeSource = "snooze";
    public const string ManualSource = "manual";
    public const string CallSource = "call";
    public const string FocusSource = "focus";
    public static string LongCallMessage => Str.Get("Nudge_LongCall_Message");

    private static readonly NudgeKind[] Scheduled = [NudgeKind.Stretch, NudgeKind.Eyes, NudgeKind.Water];
    private static readonly TimeSpan PostureSlack = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly DeskPalSettings _settings;
    private readonly StretchLibrary _library;
    private readonly object _gate = new();
    private readonly Dictionary<NudgeKind, DateTimeOffset> _due = [];
    private readonly Dictionary<NudgeKind, DateTimeOffset> _pending = [];
    private readonly List<Hold> _holds = [];
    private readonly List<DateTimeOffset> _visible = [];
    private DateTimeOffset? _lastShown;
    private DateTimeOffset? _callStart;
    private bool _longCall;
    private DateTimeOffset? _lastPosture;
    private bool _standing;
    private DateOnly _countersDate;
    private int _stretchesToday;
    private int _eyeBreaksToday;
    private int _walksToday;
    private bool _snoozedPending;
    private readonly StreakBook _book;

    public NudgeScheduler(TimeProvider time, DeskPalSettings settings, DeskPalState? state = null)
    {
        _time = time;
        _settings = settings;
        _library = new StretchLibrary(state?.LastStretchId);
        StretchesCompleted = state?.StretchesCompleted ?? 0;
        _countersDate = state?.CountersDate ?? default;
        _stretchesToday = state?.StretchesToday ?? 0;
        _eyeBreaksToday = state?.EyeBreaksToday ?? 0;
        _walksToday = state?.WalksToday ?? 0;
        CarePoints = state?.CarePoints ?? 0;
        _book = state?.Streaks ?? new StreakBook();
        _book.Normalize();
        var now = Now;
        RollDay(now);
        ApplyDue(now);
        if (state is not null)
            foreach (var h in state.Holds)
                if (h.Until > now && h.Source is not ("dnd" or CallSource)) _holds.Add(h);
    }

    public event Action<SchedulerEvent>? Event;

    public Nudge? Current { get; private set; }
    public int StretchesCompleted { get; private set; }
    public int CarePoints { get; private set; }

    public int StretchesToday
    {
        get { lock (_gate) { RollDay(Now); return _stretchesToday; } }
    }

    public int EyeBreaksToday
    {
        get { lock (_gate) { RollDay(Now); return _eyeBreaksToday; } }
    }

    public int WalksToday
    {
        get { lock (_gate) { RollDay(Now); return _walksToday; } }
    }

    private DateTimeOffset Now => _time.GetLocalNow();

    private bool WalkDue => _settings.WalkAfterStretches > 0 && StretchesCompleted >= _settings.WalkAfterStretches;

    // Call after the settings object changed: restarts the timers of the reminders that are still on.
    public void ApplySettings()
    {
        lock (_gate)
        {
            ApplyDue(Now);
            _lastPosture = null;
        }
    }

    private void ApplyDue(DateTimeOffset now)
    {
        foreach (var kind in Scheduled)
        {
            if (!_settings.IsScheduled(kind))
            {
                _due.Remove(kind);
                _pending.Remove(kind);
            }
            else if (!_pending.ContainsKey(kind))
                _due[kind] = NextOnGrid(now, _settings.IntervalFor(kind));
        }
    }

    public IReadOnlyList<Hold> Holds
    {
        get { lock (_gate) return _holds.ToList(); }
    }

    public Hold? ManualHold
    {
        get { lock (_gate) return _holds.FirstOrDefault(h => h.Source == ManualSource); }
    }

    public Hold? FocusHold
    {
        get { lock (_gate) return _holds.FirstOrDefault(h => h.Source == FocusSource); }
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
                var candidates = _due.Where(p => p.Key is NudgeKind.Stretch or NudgeKind.Eyes).ToList();
                if (candidates.Count == 0) return null;
                var best = candidates.OrderBy(p => p.Value).First();
                var kind = best.Key == NudgeKind.Stretch && WalkDue ? NudgeKind.Walk : best.Key;
                return (kind, best.Value);
            }
        }
    }

    public DeskPalState Snapshot()
    {
        lock (_gate)
        {
            RollDay(Now);
            return new DeskPalState
            {
                Holds = _holds.Where(h => h.Source is not ("dnd" or CallSource)).ToList(),
                StretchesCompleted = StretchesCompleted,
                LastStretchId = _library.LastId,
                CountersDate = _countersDate,
                StretchesToday = _stretchesToday,
                EyeBreaksToday = _eyeBreaksToday,
                WalksToday = _walksToday,
                CarePoints = CarePoints,
                Streaks = _book,
            };
        }
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
            if (source == ManualSource) { RollDay(Now); _book.PausedDate = _countersDate; }
            var hold = new Hold(source, reason, until, seated);
            var index = _holds.FindIndex(h => h.Source == source);
            if (index >= 0)
            {
                _holds[index] = hold;
                return;
            }
            _holds.Add(hold);
            if (source == CallSource) _callStart = Now;
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
            HoldEnded(hold, Now);
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
                HoldEnded(hold, now);
            }
            RollDay(now);

            if (!IsWorkingTime(now))
            {
                if (Current is not null) return;
                var restart = NextWorkStart(now);
                foreach (var kind in _pending.Keys.ToList())
                    _due[kind] = restart + _settings.IntervalFor(kind);
                _pending.Clear();
                _longCall = false;
                foreach (var kind in Scheduled)
                    if (_due.TryGetValue(kind, out var d) && d <= now)
                        _due[kind] = restart + _settings.IntervalFor(kind);
                return;
            }

            var cueDue = _due.TryGetValue(NudgeKind.Eyes, out var eyes) && eyes <= now;
            if (cueDue) _due[NudgeKind.Eyes] = NextOnGrid(now, _settings.IntervalFor(NudgeKind.Eyes));

            if (Current is null)
            {
                if (_due.TryGetValue(NudgeKind.Stretch, out var stretch) && stretch <= now)
                {
                    _pending[NudgeKind.Stretch] = stretch;
                    _due.Remove(NudgeKind.Stretch);
                }

                if (_settings.Water == WaterMode.Hourly && !_pending.ContainsKey(NudgeKind.Water)
                    && _due.TryGetValue(NudgeKind.Water, out var hourly) && hourly <= now
                    && !(_due.TryGetValue(NudgeKind.Stretch, out var soon) && soon <= now + MergeWindow)
                    && !_pending.ContainsKey(NudgeKind.Stretch))
                {
                    _pending[NudgeKind.Water] = hourly;
                    _due.Remove(NudgeKind.Water);
                }

                if (_pending.Count > 0 && _holds.Count == 0
                    && (_lastShown is not { } last || now - last >= MinGap)
                    && BudgetLeft(now))
                {
                    var batch = new List<NudgeKind>();
                    if (_pending.ContainsKey(NudgeKind.Stretch))
                    {
                        batch.Add(NudgeKind.Stretch);
                        if (_pending.ContainsKey(NudgeKind.Water)
                            || (_due.TryGetValue(NudgeKind.Water, out var water) && water <= now + MergeWindow))
                            batch.Add(NudgeKind.Water);
                    }
                    else batch.Add(NudgeKind.Water);
                    Show(batch, now, isTest: false, longCall: _longCall);
                    _longCall = false;
                    return;
                }
            }

            // The eye cue is silent and outside the budget; it is skipped while anything else is going on.
            if (cueDue && Current is null && _holds.Count == 0 && _pending.Count == 0)
            {
                _eyeBreaksToday++;
                var cue = new Nudge(Guid.NewGuid(), [NudgeKind.Eyes], NudgeKind.Eyes, Str.Get("Nudge_EyeCue_Title"),
                    Str.Get("Nudge_EyeCue_Message"), null, null, 20, "bloop-idle", false, now);
                Raise(new SchedulerEvent(SchedulerEventType.EyeCue, cue));
            }
        }
    }

    public bool Execute(Command command)
    {
        switch (command)
        {
            case DoneCommand c: return Answer(c.NudgeId, SchedulerEventType.NudgeDone, c.Completion);
            case SkipCommand c: return Answer(c.NudgeId, SchedulerEventType.NudgeSkipped);
            case SnoozeCommand c: return Snooze(c.NudgeId);
            case TestNudgeCommand: return RequestNudge();
            case PauseCommand c: return Pause(c);
            case ResumeCommand: return EndHold(ManualSource);
            case BreakNowCommand c: return BreakNow(c.Kind);
            case FocusCommand c when c.Minutes > 0: StartHold(FocusSource, Str.Get("Nudge_Hold_Focus"), Now.AddMinutes(c.Minutes)); return true;
            case FocusOffCommand: return EndHold(FocusSource);
            default: return false;
        }
    }

    public bool RequestNudge()
    {
        lock (_gate)
        {
            if (Current is not null) return false;
            Show([NudgeKind.Stretch], Now, isTest: true, longCall: false);
            return true;
        }
    }

    private bool BreakNow(NudgeKind kind)
    {
        if (kind is not (NudgeKind.Stretch or NudgeKind.Water or NudgeKind.Eyes or NudgeKind.Walk)) return false;
        lock (_gate)
        {
            if (Current is not null) return false;
            Show([kind], Now, isTest: false, longCall: false);
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

    private void HoldEnded(Hold hold, DateTimeOffset now)
    {
        Raise(new SchedulerEvent(SchedulerEventType.HoldEnded, Hold: hold));
        if (hold.Source != CallSource || _callStart is not { } start) return;
        if (!_settings.MovementEnabled) { _callStart = null; return; }
        _callStart = null;
        if (now - start < LongCall) return;
        _pending[NudgeKind.Stretch] = now;
        _due.Remove(NudgeKind.Stretch);
        _longCall = true;
    }

    private bool BudgetLeft(DateTimeOffset now)
    {
        _visible.RemoveAll(t => now - t >= BudgetWindow);
        return _visible.Count < MaxVisiblePerHour;
    }

    private void RollDay(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        if (today == _countersDate) return;
        if (_countersDate != default) _book.Archive(RecordFor(_countersDate, _book.PausedDate == _countersDate));
        _countersDate = today;
        _stretchesToday = 0;
        _eyeBreaksToday = 0;
        _walksToday = 0;
    }

    private DayRecord RecordFor(DateOnly date, bool paused)
    {
        var goals = DailyGoals.For(_settings);
        return new DayRecord
        {
            Date = date,
            Stretches = _stretchesToday, EyeBreaks = _eyeBreaksToday, Walks = _walksToday,
            GoalStretches = goals.Stretches, GoalEyes = goals.EyeBreaks, GoalWalks = goals.Walks,
            Relaxed = paused || Array.IndexOf(_settings.WorkingHours.Days, date.DayOfWeek) < 0,
        };
    }

    // Current and best streaks per goal and overall. Updates the stored bests; a new best is reported until acknowledged.
    public StreakSummary Streaks
    {
        get
        {
            lock (_gate)
            {
                var now = Now;
                RollDay(now);
                return _book.Summarize(RecordFor(_countersDate, _book.PausedDate == _countersDate));
            }
        }
    }

    public void AcknowledgeStreakBests()
    {
        lock (_gate) _book.AcknowledgeBests();
    }

    public WeekRecap? LastWeekRecap(DayOfWeek firstDay)
    {
        lock (_gate)
        {
            RollDay(Now);
            return StreakMath.BuildRecap(_book.History, _countersDate, firstDay);
        }
    }

    public bool RecapDue(DayOfWeek firstDay)
    {
        lock (_gate)
        {
            RollDay(Now);
            return _book.RecapDue(_countersDate, firstDay);
        }
    }

    public void MarkRecapShown(DayOfWeek firstDay)
    {
        lock (_gate)
        {
            RollDay(Now);
            _book.MarkRecapShown(_countersDate, firstDay);
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
            reason = Str.Get("Nudge_Hold_PausedUntilTomorrow");
        }
        else if (c.Minutes is > 0)
        {
            until = now.AddMinutes(c.Minutes.Value);
            reason = Str.Get("Nudge_Hold_Paused");
        }
        else return false;
        StartHold(ManualSource, reason, until);
        return true;
    }

    private bool Answer(Guid? id, SchedulerEventType type, double completion = 1)
    {
        lock (_gate)
        {
            if (Current is null || (id is not null && id != Current.Id)) return false;
            var nudge = Current;
            var now = Now;
            var points = 0;
            if (!nudge.IsTest)
            {
                Reschedule(nudge, now);
                if (type == SchedulerEventType.NudgeDone)
                {
                    RollDay(now);
                    if (nudge.Kinds.Contains(NudgeKind.Walk)) { StretchesCompleted = 0; _walksToday++; }
                    else if (nudge.Kinds.Contains(NudgeKind.Stretch)) StretchesCompleted++;
                    if (nudge.Kinds.Contains(NudgeKind.Walk) || nudge.Kinds.Contains(NudgeKind.Stretch)) _stretchesToday++;
                    else if (nudge.Primary == NudgeKind.Eyes) _eyeBreaksToday++;
                    points = NudgeReward.Points(completion, nudge.TimedSeconds > 0, nudge.WasSnoozed);
                    CarePoints += points;
                }
            }
            Current = null;
            Raise(new SchedulerEvent(type, nudge, Points: points));
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
                    if (kind == NudgeKind.Water) _due[NudgeKind.Water] = now;
                    else if (kind is NudgeKind.Stretch or NudgeKind.Walk) _pending[NudgeKind.Stretch] = now;
                }
            if (!nudge.IsTest) _snoozedPending = true;
            _lastShown = null;
            Raise(new SchedulerEvent(SchedulerEventType.NudgeSnoozed, nudge));
            StartHold(SnoozeSource, Str.Get("Nudge_Hold_Snoozed"), now + SnoozeDuration);
            return true;
        }
    }

    private void Reschedule(Nudge nudge, DateTimeOffset now)
    {
        if (nudge.IsTest) return;
        foreach (var kind in nudge.Kinds)
        {
            var key = kind == NudgeKind.Walk ? NudgeKind.Stretch : kind;
            if (Array.IndexOf(Scheduled, key) >= 0 && _settings.IsScheduled(key)) _due[key] = NextOnGrid(now, _settings.IntervalFor(key));
        }
    }

    // All intervals are multiples of 20 minutes counted from the start of the working day, so they line up.
    private DateTimeOffset NextOnGrid(DateTimeOffset now, TimeSpan interval)
    {
        if (!IsWorkingTime(now)) return NextWorkStart(now) + interval;
        var origin = new DateTimeOffset(now.Date + _settings.WorkingHours.Start.ToTimeSpan(), now.Offset);
        var steps = (long)Math.Floor((now - origin) / interval) + 1;
        return origin + TimeSpan.FromTicks(interval.Ticks * steps);
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

    private void Show(List<NudgeKind> batch, DateTimeOffset now, bool isTest, bool longCall)
    {
        if (!isTest)
            foreach (var kind in batch)
            {
                _pending.Remove(kind);
                _due.Remove(kind);
            }

        var kinds = batch.Distinct()
            .Select(k => k == NudgeKind.Stretch && !longCall && WalkDue ? NudgeKind.Walk : k)
            .OrderBy(k => k == NudgeKind.Walk ? -1 : (int)k)
            .ToList();
        var primary = kinds[0];
        var stretch = primary == NudgeKind.Stretch ? _library.Next() : null;
        var (title, instruction) = Describe(primary, stretch);
        string? posture = null;
        if (!isTest && !longCall && primary == NudgeKind.Stretch && _settings.SitStandEnabled
            && (_lastPosture is not { } lp || now - lp >= TimeSpan.FromMinutes(_settings.SitStandMinutes) - PostureSlack))
        {
            posture = Str.Get(_standing ? "Nudge_Posture_Sit" : "Nudge_Posture_Stand");
            _standing = !_standing;
            _lastPosture = now;
        }
        string message;
        string? extra = null;
        if (longCall)
        {
            message = LongCallMessage;
            extra = instruction;
        }
        else if (kinds.Count == 1)
        {
            message = posture ?? instruction;
            if (posture is not null) extra = instruction;
        }
        else
        {
            message = Str.Format("Nudge_Combined", posture ?? title, string.Join(Str.Get("Nudge_And"), kinds.Skip(1).Select(Tail)));
            extra = instruction;
        }

        int? timed = primary switch
        {
            NudgeKind.Stretch => stretch!.DurationSeconds,
            NudgeKind.Breathe => 60,
            _ => null,
        };
        var asset = primary switch
        {
            NudgeKind.Stretch => stretch!.AssetName,
            NudgeKind.Walk => "bloop-walk",
            _ => "bloop-idle",
        };

        var nudge = new Nudge(Guid.NewGuid(), kinds, primary, title, message, extra, stretch, timed, asset, isTest, now, !isTest && _snoozedPending, longCall);
        if (!isTest) _snoozedPending = false;
        Current = nudge;
        if (!isTest)
        {
            _lastShown = now;
            _visible.Add(now);
        }
        Raise(new SchedulerEvent(SchedulerEventType.NudgeShown, nudge));
    }

    private static (string Title, string Instruction) Describe(NudgeKind kind, StretchInfo? stretch) => kind switch
    {
        NudgeKind.Stretch => (Str.Get("Nudge_Stretch_Title"), Str.Format("Nudge_Stretch_Text", stretch!.Name, stretch.Instruction)),
        NudgeKind.Walk => (Str.Get("Nudge_Walk_Title"), Str.Get("Nudge_Walk_Text")),
        NudgeKind.Eyes => (Str.Get("Nudge_Eyes_Title"), Str.Get("Nudge_Eyes_Text")),
        NudgeKind.Water => (Str.Get("Nudge_Water_Title"), Str.Get("Nudge_Water_Text")),
        _ => (Str.Get("Nudge_Breathe_Title"), Str.Get("Nudge_Breathe_Text")),
    };

    private static string Tail(NudgeKind kind) => kind switch
    {
        NudgeKind.Stretch => Str.Get("Nudge_Stretch_Tail"),
        NudgeKind.Walk => Str.Get("Nudge_Walk_Tail"),
        NudgeKind.Eyes => Str.Get("Nudge_Eyes_Tail"),
        NudgeKind.Water => Str.Get("Nudge_Water_Tail"),
        _ => Str.Get("Nudge_Breathe_Tail"),
    };

    private void Raise(SchedulerEvent e) => Event?.Invoke(e);
}
