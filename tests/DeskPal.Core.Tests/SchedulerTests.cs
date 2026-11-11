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
    public void Eyes_comes_due_first_at_20_minutes()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(20));
        Assert.Equal(NudgeKind.Eyes, s.Current!.Primary);
        Assert.Equal(1, Shown(ev));
    }

    [Fact]
    public void Nudges_due_within_five_minutes_merge_into_one()
    {
        var settings = new DeskPalSettings { StretchMinutes = 30, EyesMinutes = 30, WaterMinutes = 33 };
        var (s, t, ev) = Make(settings);
        Run(s, t, TimeSpan.FromMinutes(30));
        var n = s.Current!;
        Assert.Equal(1, Shown(ev));
        Assert.Contains(NudgeKind.Water, n.Kinds);
        Assert.Contains(NudgeKind.Stretch, n.Kinds);
        Assert.Contains("grab some water while you're up", n.Message);
        Assert.StartsWith("Stretch time, ", n.Message);
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
    public void Min_gap_delays_next_nudge_but_not_user_request()
    {
        var settings = new DeskPalSettings { StretchMinutes = 30, EyesMinutes = 20, WaterMinutes = 45 };
        var (s, t, ev) = Make(settings);
        Run(s, t, TimeSpan.FromMinutes(20));
        var first = s.Current!;
        s.Execute(new DoneCommand(first.Id));
        // eyes next due at 40; stretch at 30 (shown 20, so gap satisfied at 30)
        Run(s, t, TimeSpan.FromMinutes(9));
        Assert.Null(s.Current); // 29 min, nothing due
        Run(s, t, TimeSpan.FromMinutes(1));
        Assert.NotNull(s.Current); // 30 min, stretch due and 10 min since last shown

        s.Execute(new DoneCommand());
        Assert.True(s.RequestNudge()); // immediately, gap ignored
        Assert.True(s.Current!.IsTest);
    }

    [Fact]
    public void Gap_blocks_a_nudge_that_comes_due_too_soon()
    {
        var settings = new DeskPalSettings { StretchMinutes = 45, EyesMinutes = 20, WaterMinutes = 60 };
        var (s, t, ev) = Make(settings);
        Run(s, t, TimeSpan.FromMinutes(20));
        s.Execute(new SkipCommand()); // eyes due again at 40
        Run(s, t, TimeSpan.FromMinutes(25)); // 45: stretch due, merged window handled; eyes was due at 40 (shown 20)
        Assert.NotNull(s.Current);
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
        Assert.True(s.PendingCount >= 3);
        Run(s, t, TimeSpan.FromMinutes(2));
        Assert.Equal(1, Shown(ev));
        Assert.Equal(3, s.Current!.Kinds.Count);
        s.Execute(new DoneCommand());
        Run(s, t, TimeSpan.FromMinutes(5));
        Assert.Equal(1, Shown(ev));
    }

    [Fact]
    public void Snooze_holds_five_minutes_then_one_nudge_returns()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(20));
        s.Execute(new SnoozeCommand());
        Assert.Null(s.Current);
        Assert.Equal("snooze", s.Holds.Single().Source);
        Run(s, t, TimeSpan.FromMinutes(4));
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
        var settings = new DeskPalSettings { StretchMinutes = 20, EyesMinutes = 60, WaterMinutes = 60 };
        settings.WorkingHours.End = new TimeOnly(23, 0);
        var (s, t, ev) = Make(settings);
        for (var i = 0; i < 3; i++)
        {
            Run(s, t, TimeSpan.FromMinutes(20));
            Assert.Equal(NudgeKind.Stretch, s.Current!.Primary);
            s.Execute(new DoneCommand());
        }
        Assert.Equal(3, s.StretchesCompleted);
        Run(s, t, TimeSpan.FromMinutes(20));
        Assert.Equal(NudgeKind.Walk, s.Current!.Primary);
        s.Execute(new DoneCommand());
        Assert.Equal(0, s.StretchesCompleted);
        Run(s, t, TimeSpan.FromMinutes(20));
        Assert.Equal(NudgeKind.Stretch, s.Current!.Primary);
    }

    [Fact]
    public void Stale_command_for_old_nudge_is_ignored()
    {
        var (s, t, _) = Make();
        Run(s, t, TimeSpan.FromMinutes(20));
        Assert.False(s.Execute(new DoneCommand(Guid.NewGuid())));
        Assert.NotNull(s.Current);
    }

    [Fact]
    public void Expire_clears_nudge_without_done_or_skip()
    {
        var (s, t, ev) = Make();
        Run(s, t, TimeSpan.FromMinutes(20));
        s.Expire(s.Current!.Id);
        Assert.Null(s.Current);
        Assert.DoesNotContain(ev, e => e.Type is SchedulerEventType.NudgeDone or SchedulerEventType.NudgeSkipped);
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

