using DeskPal.Core;

namespace DeskPal.Core.Tests;

public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTime(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => _now += by;
}

public class SchedulerTests
{
    // Wednesday 2026-11-11 09:00 UTC
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static (NudgeScheduler s, FakeTime t, List<SchedulerEvent> ev) Make(DeskPalSettings? settings = null, DateTimeOffset? start = null)
    {
        var t = new FakeTime(start ?? Start);
        var s = new NudgeScheduler(t, settings ?? new DeskPalSettings());
        var ev = new List<SchedulerEvent>();
        s.Event += ev.Add;
        return (s, t, ev);
    }

    private static int Shown(List<SchedulerEvent> ev) => ev.Count(e => e.Type == SchedulerEventType.NudgeShown);

    private static void Run(NudgeScheduler s, FakeTime t, TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromMinutes(1))
        {
            t.Advance(TimeSpan.FromMinutes(1));
            s.Tick();
        }
    }

    [Fact]
    public void Eyes_is_a_silent_cue_at_20_minutes_not_a_nudge()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(20));
        Assert.Null(s.Current);
        Assert.Equal(0, Shown(ev));
        Assert.Equal(1, ev.Count(e => e.Type == SchedulerEventType.EyeCue));
        Assert.Equal(1, s.EyeBreaksToday);
    }

    [Fact]
    public void Movement_comes_at_30_minutes_with_water_folded_in()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        var n = s.Current!;
        Assert.Equal(1, Shown(ev));
        Assert.Contains(NudgeKind.Water, n.Kinds);
        Assert.Contains(NudgeKind.Stretch, n.Kinds);
        Assert.Contains("grab some water while you're up", n.Message);
        Assert.StartsWith("Stretch time, ", n.Message);
    }

    [Fact]
    public void Water_never_comes_on_its_own()
    {
        var settings = new DeskPalSettings { StretchMinutes = 60 };
        var (s, t, ev) = Make(settings);
        Run(s, t, TimeSpan.FromMinutes(59));
        Assert.Equal(0, Shown(ev));
        Run(s, t, TimeSpan.FromMinutes(1));
        Assert.Equal(1, Shown(ev));
        Assert.All(ev.Where(e => e.Type == SchedulerEventType.NudgeShown), e => Assert.Contains(NudgeKind.Stretch, e.Nudge!.Kinds));
    }

    [Fact]
    public void Everything_sits_on_the_working_day_grid()
    {
        var (s, t, ev) = Make();
        for (var i = 0; i < 120; i++)
        {
            t.Advance(TimeSpan.FromMinutes(1));
            s.Tick();
            if (s.Current is not null) s.Execute(new DoneCommand());
        }
        var cues = ev.Where(e => e.Type == SchedulerEventType.EyeCue).Select(e => (e.Nudge!.ShownAt - Start).TotalMinutes).ToList();
        var shown = ev.Where(e => e.Type == SchedulerEventType.NudgeShown).Select(e => (e.Nudge!.ShownAt - Start).TotalMinutes).ToList();
        Assert.Equal([20.0, 40.0, 80.0, 100.0], cues);
        Assert.Equal([30.0, 60.0, 90.0, 120.0], shown);
    }

    [Fact]
    public void Never_more_than_one_visible_nudge()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(120));
        Assert.Equal(1, Shown(ev));
        Assert.False(s.RequestNudge());
    }

    [Fact]
    public void At_most_two_visible_interruptions_an_hour()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        s.Execute(new SnoozeCommand()); // shown #1 at 30, back at 40 as #2
        Run(s, t, TimeSpan.FromMinutes(10));
        Assert.Equal(2, Shown(ev));
        s.Execute(new DoneCommand());
        Run(s, t, TimeSpan.FromMinutes(20)); // 60: movement is due but the hour already holds two
        Assert.Equal(2, Shown(ev));
        Assert.Null(s.Current);
        Run(s, t, TimeSpan.FromMinutes(30)); // 90: the one from minute 30 has aged out
        Assert.Equal(3, Shown(ev));
    }

    [Fact]
    public void Eye_cue_does_not_use_the_budget()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.Equal(1, ev.Count(e => e.Type == SchedulerEventType.EyeCue));
        Run(s, t, TimeSpan.FromMinutes(40));
        s.Execute(new SnoozeCommand());
        Run(s, t, TimeSpan.FromMinutes(10));
        Assert.Equal(2, Shown(ev));
    }

    [Fact]
    public void Eye_cue_respects_holds_and_a_visible_nudge()
    {
        var (s, t, ev) = Make();
        s.StartHold("call", "In a call", t.GetLocalNow().AddMinutes(30), seated: true);
        Run(s, t, TimeSpan.FromMinutes(25));
        Assert.DoesNotContain(ev, e => e.Type == SchedulerEventType.EyeCue);
        Assert.Equal(0, s.EyeBreaksToday);
    }

    [Fact]
    public void Min_gap_does_not_apply_to_user_request()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        s.Execute(new DoneCommand());
        Assert.True(s.RequestNudge());
        Assert.True(s.Current!.IsTest);
    }

    [Fact]
    public void Test_nudge_does_not_swallow_the_schedule()
    {
        var (s, t, ev) = Make();
        Assert.True(s.RequestNudge());
        s.Execute(new DoneCommand());
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.Equal(2, Shown(ev));
    }

    [Fact]
    public void Holds_overlap_and_expire_independently()
    {
        var (s, t, ev) = Make();
        s.StartHold("call", "In a call", t.GetLocalNow().AddMinutes(60));
        s.StartHold("manual", "Paused", t.GetLocalNow().AddMinutes(30));
        Run(s, t, TimeSpan.FromMinutes(45));
        Assert.Single(s.Holds);
        Assert.Equal("call", s.Holds[0].Source);
        Assert.Null(s.Current);
        Assert.Equal(1, ev.Count(e => e.Type == SchedulerEventType.HoldEnded));
        Run(s, t, TimeSpan.FromMinutes(16));
        Assert.Empty(s.Holds);
        Assert.NotNull(s.Current);
    }

    [Fact]
    public void Catch_up_after_hold_is_exactly_one_merged_nudge()
    {
        var (s, t, ev) = Make();
        s.StartHold("manual", "Paused", t.GetLocalNow().AddMinutes(90));
        Run(s, t, TimeSpan.FromMinutes(89));
        Assert.Equal(0, Shown(ev));
        Assert.Equal(1, s.PendingCount);
        Run(s, t, TimeSpan.FromMinutes(2));
        Assert.Equal(1, Shown(ev));
        Assert.Equal(2, s.Current!.Kinds.Count);
        s.Execute(new DoneCommand());
        Run(s, t, TimeSpan.FromMinutes(5));
        Assert.Equal(1, Shown(ev));
    }

    [Fact]
    public void Snooze_holds_ten_minutes_then_one_nudge_returns()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        s.Execute(new SnoozeCommand());
        Assert.Null(s.Current);
        Assert.Equal("snooze", s.Holds.Single().Source);
        Run(s, t, TimeSpan.FromMinutes(9));
        Assert.Null(s.Current);
        Run(s, t, TimeSpan.FromMinutes(1));
        Assert.NotNull(s.Current);
        Assert.Equal(2, Shown(ev));
    }
    [Fact]
    public void Pause_and_resume_via_router()
    {
        var (s, t, ev) = Make();
        s.Execute(new PauseCommand(Minutes: 30));
        Assert.NotNull(s.ManualHold);
        s.Execute(new ResumeCommand());
        Assert.Null(s.ManualHold);
        s.Execute(new PauseCommand(UntilTomorrow: true));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), s.ManualHold!.Until);
    }

    [Fact]
    public void No_nudges_outside_working_hours()
    {
        var evening = new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero);
        var (s, t, ev) = Make(start: evening);
        Run(s, t, TimeSpan.FromMinutes(60)); // 19:00
        Assert.Equal(0, Shown(ev));
        Assert.False(s.IsWorkingTime(new DateTimeOffset(2026, 11, 14, 11, 0, 0, TimeSpan.Zero))); // Saturday
        Assert.True(s.IsWorkingTime(new DateTimeOffset(2026, 11, 13, 17, 29, 0, TimeSpan.Zero)));
        Assert.False(s.IsWorkingTime(new DateTimeOffset(2026, 11, 13, 17, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Nudges_resume_next_morning()
    {
        var evening = new DateTimeOffset(2026, 11, 13, 17, 0, 0, TimeSpan.Zero); // Friday
        var (s, t, ev) = Make(start: evening);
        t.Advance(TimeSpan.FromHours(2));
        s.Tick();
        t.Advance(TimeSpan.FromDays(3)); // Monday-ish 19:00
        s.Tick();
        Assert.Equal(0, Shown(ev));
        Assert.True(s.NextDue!.Value.At > t.GetLocalNow());
    }

    [Fact]
    public void Walk_replaces_stretch_after_three_completed_stretches()
    {
        var settings = new DeskPalSettings();
        settings.WorkingHours.End = new TimeOnly(23, 0);
        var (s, t, ev) = Make(settings);
        for (var i = 0; i < 3; i++)
        {
            Run(s, t, TimeSpan.FromMinutes(30));
            Assert.Equal(NudgeKind.Stretch, s.Current!.Primary);
            s.Execute(new DoneCommand());
        }
        Assert.Equal(3, s.StretchesCompleted);
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.Equal(NudgeKind.Walk, s.Current!.Primary);
        s.Execute(new DoneCommand());
        Assert.Equal(0, s.StretchesCompleted);
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.Equal(NudgeKind.Stretch, s.Current!.Primary);
    }

    [Fact]
    public void Stale_command_for_old_nudge_is_ignored()
    {
        var (s, t, _) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.False(s.Execute(new DoneCommand(Guid.NewGuid())));
        Assert.NotNull(s.Current);
    }

    [Fact]
    public void Expire_clears_nudge_without_done_or_skip()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        s.Expire(s.Current!.Id);
        Assert.Null(s.Current);
        Assert.DoesNotContain(ev, e => e.Type is SchedulerEventType.NudgeDone or SchedulerEventType.NudgeSkipped);
    }

    private static DeskPalSettings AllDay(Action<DeskPalSettings>? tweak = null)
    {
        var s = new DeskPalSettings();
        s.WorkingHours.End = new TimeOnly(23, 0);
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void Migration_moves_the_old_40_minute_default_to_30_once()
    {
        var old = new DeskPalSettings { StretchMinutes = 40, SettingsVersion = 0 };
        Assert.True(old.Migrate());
        Assert.Equal(30, old.StretchMinutes);
        Assert.Equal(DeskPalSettings.CurrentVersion, old.SettingsVersion);
        old.StretchMinutes = 40;
        Assert.False(old.Migrate());
        Assert.Equal(40, old.StretchMinutes);
        var sixty = new DeskPalSettings { StretchMinutes = 60 };
        sixty.Migrate();
        Assert.Equal(60, sixty.StretchMinutes);
    }

    [Fact]
    public void Old_settings_file_without_new_fields_loads_and_migrates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"StretchMinutes\": 40, \"WaterMinutes\": 40, \"AvatarStyle\": \"soft3d\" }");
            var loaded = JsonFileStore.Load<DeskPalSettings>(path, new FakeTime(Start));
            loaded.Migrate();
            Assert.Equal(30, loaded.StretchMinutes);
            Assert.Equal("soft3d", loaded.AvatarStyle);
            Assert.True(loaded.EyesEnabled);
            Assert.Equal(WaterMode.WithBreaks, loaded.Water);
        }
        finally
        {
            if (Directory.Exists(dir) && dir.Contains("deskpal-test-")) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Movement_interval_is_clamped_and_followed_by_the_scheduler()
    {
        var settings = new DeskPalSettings { StretchMinutes = 45 };
        var (s, t, ev) = Make(settings);
        Run(s, t, TimeSpan.FromMinutes(44));
        Assert.Equal(0, Shown(ev));
        Run(s, t, TimeSpan.FromMinutes(1));
        Assert.Equal(1, Shown(ev));
        Assert.Equal(15, new DeskPalSettings { StretchMinutes = 5 }.MovementMinutes);
        Assert.Equal(120, new DeskPalSettings { StretchMinutes = 500 }.MovementMinutes);
    }

    [Fact]
    public void Eyes_off_gives_no_cue_and_eyes_30_follows_the_choice()
    {
        var (s, t, ev) = Make(new DeskPalSettings { EyesEnabled = false });
        Run(s, t, TimeSpan.FromMinutes(25));
        Assert.DoesNotContain(ev, e => e.Type == SchedulerEventType.EyeCue);
        var (s2, t2, ev2) = Make(new DeskPalSettings { EyesMinutes = 30, MovementEnabled = false });
        Run(s2, t2, TimeSpan.FromMinutes(25));
        Assert.DoesNotContain(ev2, e => e.Type == SchedulerEventType.EyeCue);
        Run(s2, t2, TimeSpan.FromMinutes(5));
        Assert.Single(ev2, e => e.Type == SchedulerEventType.EyeCue);
    }

    [Fact]
    public void Water_off_leaves_movement_alone()
    {
        var (s, t, _) = Make(new DeskPalSettings { Water = WaterMode.Off });
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.Equal([NudgeKind.Stretch], s.Current!.Kinds);
    }

    [Fact]
    public void Hourly_water_merges_with_movement_at_the_hour_and_stands_alone_between()
    {
        var (s, t, ev) = Make(AllDay(x => { x.Water = WaterMode.Hourly; x.StretchMinutes = 45; }));
        Run(s, t, TimeSpan.FromMinutes(45));
        Assert.Equal([NudgeKind.Stretch], s.Current!.Kinds);
        s.Execute(new DoneCommand());
        Run(s, t, TimeSpan.FromMinutes(15)); // 60: water is due, movement not for another 30
        Assert.Equal([NudgeKind.Water], s.Current!.Kinds);
        s.Execute(new DoneCommand());
        Run(s, t, TimeSpan.FromMinutes(45)); // 105: movement was due at 90 but waited for the budget
        Assert.Equal([NudgeKind.Stretch], s.Current!.Kinds);
    }

    [Fact]
    public void Hourly_water_counts_toward_the_two_an_hour_budget()
    {
        var (s, t, ev) = Make(AllDay(x => { x.Water = WaterMode.Hourly; x.StretchMinutes = 20; }));
        for (var i = 0; i < 180; i++)
        {
            t.Advance(TimeSpan.FromMinutes(1));
            s.Tick();
            if (s.Current is not null) s.Execute(new DoneCommand());
            var recent = ev.Count(e => e.Type == SchedulerEventType.NudgeShown && t.GetLocalNow() - e.Nudge!.ShownAt < TimeSpan.FromHours(1));
            Assert.True(recent <= NudgeScheduler.MaxVisiblePerHour);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Walk_after_follows_the_setting(int after)
    {
        var (s, t, _) = Make(AllDay(x => x.WalkAfterStretches = after));
        for (var i = 0; i < after; i++)
        {
            Run(s, t, TimeSpan.FromMinutes(30));
            Assert.Equal(NudgeKind.Stretch, s.Current!.Primary);
            s.Execute(new DoneCommand());
        }
        Run(s, t, TimeSpan.FromMinutes(30));
        Assert.Equal(NudgeKind.Walk, s.Current!.Primary);
    }

    [Fact]
    public void Walk_off_never_escalates()
    {
        var (s, t, _) = Make(AllDay(x => x.WalkAfterStretches = 0));
        for (var i = 0; i < 6; i++)
        {
            Run(s, t, TimeSpan.FromMinutes(30));
            Assert.Equal(NudgeKind.Stretch, s.Current!.Primary);
            s.Execute(new DoneCommand());
        }
    }

    [Fact]
    public void Sit_stand_alternates_every_other_nudge_at_60_minutes()
    {
        var (s, t, _) = Make(AllDay(x => { x.SitStandEnabled = true; x.SitStandMinutes = 60; x.WalkAfterStretches = 0; }));
        var messages = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            Run(s, t, TimeSpan.FromMinutes(30));
            messages.Add(s.Current!.Message);
            s.Execute(new DoneCommand());
        }
        Assert.StartsWith("Switch to standing", messages[0]);
        Assert.DoesNotContain("standing", messages[1]);
        Assert.StartsWith("Time to sit for a bit", messages[2]);
        Assert.DoesNotContain("sit for", messages[3]);
    }

    [Fact]
    public void Changing_settings_reschedules_live()
    {
        var settings = AllDay();
        var (s, t, ev) = Make(settings);
        Run(s, t, TimeSpan.FromMinutes(10));
        settings.StretchMinutes = 60;
        s.ApplySettings();
        Run(s, t, TimeSpan.FromMinutes(40));
        Assert.Equal(0, Shown(ev)); // minute 50: the old 30-minute mark passed without a nudge
        settings.MovementEnabled = false;
        s.ApplySettings();
        Run(s, t, TimeSpan.FromMinutes(60));
        Assert.Equal(0, Shown(ev));
        Assert.Null(s.NextDue is { Kind: NudgeKind.Stretch } ? s.NextDue : null);
    }

    private sealed class FakeCalls : ICallDetector
    {
        public bool InCall;
        public bool IsInCall() => InCall;
    }

    private static void RunCall(CallMonitor m, NudgeScheduler s, FakeTime t, int minutes)
    {
        for (var i = 0; i < minutes; i++)
        {
            t.Advance(TimeSpan.FromMinutes(1));
            m.Poll();
            s.Tick();
        }
    }

    [Fact]
    public void Call_is_a_seated_hold_and_a_long_call_ends_with_one_merged_stretch()
    {
        var (s, t, ev) = Make();
        var calls = new FakeCalls { InCall = true };
        var m = new CallMonitor(calls, s, t);
        RunCall(m, s, t, 45);
        var hold = s.Holds.Single();
        Assert.Equal("call", hold.Source);
        Assert.Equal("In a call", hold.Reason);
        Assert.True(hold.Seated);
        Assert.Equal(0, Shown(ev));
        Assert.DoesNotContain(ev, e => e.Type == SchedulerEventType.EyeCue);
        calls.InCall = false;
        m.Poll();
        Assert.Equal(1, Shown(ev));
        Assert.True(s.Current!.IsLongCall);
    }

    [Fact]
    public void Short_call_does_not_produce_the_long_call_nudge()
    {
        var (s, t, ev) = Make();
        var calls = new FakeCalls { InCall = true };
        var m = new CallMonitor(calls, s, t);
        RunCall(m, s, t, 10);
        calls.InCall = false;
        m.Poll();
        Assert.Equal(0, Shown(ev));
        Assert.Null(s.Current);
    }

    [Fact]
    public void Daily_counters_persist_and_reset_on_a_new_day()
    {
        var (s, t, _) = Make();
        Run(s, t, TimeSpan.FromMinutes(30));
        s.Execute(new DoneCommand());
        Assert.Equal(1, s.StretchesToday);
        Assert.Equal(1, s.EyeBreaksToday);
        var s2 = new NudgeScheduler(t, new DeskPalSettings(), s.Snapshot());
        Assert.Equal(1, s2.StretchesToday);
        Assert.Equal(1, s2.EyeBreaksToday);
        t.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, s2.StretchesToday);
        Assert.Equal(0, s2.EyeBreaksToday);
    }

    [Fact]
    public void Stretch_rotation_never_repeats_area_or_stretch()
    {
        var lib = new StretchLibrary();
        StretchInfo? prev = null;
        for (var i = 0; i < 40; i++)
        {
            var n = lib.Next();
            if (prev is not null)
            {
                Assert.NotEqual(prev.Id, n.Id);
                Assert.NotEqual(prev.BodyArea, n.BodyArea);
            }
            prev = n;
        }
        Assert.Equal(8, StretchLibrary.All.Count);
        Assert.All(StretchLibrary.All, s => Assert.InRange(s.DurationSeconds, 20, 60));
    }

    [Fact]
    public void State_survives_restart_and_corrupt_settings_are_renamed()
    {
        var (s, t, _) = Make();
        s.Execute(new PauseCommand(Minutes: 30));
        var dir = Path.Combine(Path.GetTempPath(), "deskpal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var statePath = Path.Combine(dir, "state.json");
            JsonFileStore.Save(statePath, s.Snapshot());
            JsonFileStore.Save(statePath, s.Snapshot()); // exercises the replace path
            var loaded = JsonFileStore.Load<DeskPalState>(statePath, t);
            var s2 = new NudgeScheduler(t, new DeskPalSettings(), loaded);
            Assert.Equal("manual", s2.Holds.Single().Source);

            var settingsPath = Path.Combine(dir, "settings.json");
            File.WriteAllText(settingsPath, "{ not json");
            var settings = JsonFileStore.Load<DeskPalSettings>(settingsPath, t);
            Assert.Equal(30, settings.StretchMinutes);
            Assert.False(File.Exists(settingsPath));
            Assert.Single(Directory.GetFiles(dir, "settings.corrupt-*.json"));
        }
        finally
        {
            if (Directory.Exists(dir) && dir.Contains("deskpal-test-")) Directory.Delete(dir, true);
        }
    }
}

