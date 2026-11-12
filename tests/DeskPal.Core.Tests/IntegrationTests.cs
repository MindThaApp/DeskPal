using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPal.Core.Integration;

namespace DeskPal.Core.Tests;

public class EventTests
{
    // Wednesday 2026-11-11 09:00 UTC
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static (NudgeScheduler s, DeskPalEventHub hub, FakeTime t, List<DeskPalEvent> all, List<DeskPalEvent> published) Make(DeskPalSettings? settings = null)
    {
        var t = new FakeTime(Start);
        settings ??= new DeskPalSettings();
        var s = new NudgeScheduler(t, settings);
        var hub = new DeskPalEventHub(s, settings, t);
        var all = new List<DeskPalEvent>();
        var published = new List<DeskPalEvent>();
        hub.Raised += all.Add;
        hub.Published += published.Add;
        return (s, hub, t, all, published);
    }

    [Fact]
    public void Break_start_done_and_skip_become_events_with_kind_and_cause()
    {
        var (s, _, t, all, _) = Make();
        Assert.True(s.Execute(new BreakNowCommand(NudgeKind.Water)));
        t.Advance(TimeSpan.FromMinutes(2));
        Assert.True(s.Execute(new DoneCommand()));
        Assert.True(s.Execute(new BreakNowCommand(NudgeKind.Walk)));
        Assert.True(s.Execute(new SkipCommand()));

        Assert.Equal([DeskPalEventNames.BreakStarted, DeskPalEventNames.BreakDone, DeskPalEventNames.BreakStarted, DeskPalEventNames.BreakSkipped],
            all.Select(e => e.Name));
        Assert.Equal("water", all[1].Data["kind"]!.GetValue<string>());
        Assert.Equal(2, all[1].Data["minutes"]!.GetValue<int>());
        Assert.Equal("walk", all[3].Data["kind"]!.GetValue<string>());
        Assert.Equal(EventCause.Schedule, all[0].Cause);
        Assert.Equal(EventCause.User, all[1].Cause);
    }

    [Fact]
    public void Scheduled_stretch_and_eye_cue_raise_break_started()
    {
        var (s, _, t, all, _) = Make();
        for (var i = 0; i < 30; i++) { t.Advance(TimeSpan.FromMinutes(1)); s.Tick(); }
        Assert.Contains(all, e => e.Name == DeskPalEventNames.BreakStarted && e.Data["kind"]!.GetValue<string>() == "eyes");
        Assert.Contains(all, e => e.Name == DeskPalEventNames.BreakStarted && e.Data["kind"]!.GetValue<string>() == "stretch");
        Assert.All(all, e => Assert.Equal(EventCause.Schedule, e.Cause));
    }

    [Fact]
    public void Test_nudges_raise_no_events()
    {
        var (s, _, _, all, _) = Make();
        s.Execute(new TestNudgeCommand());
        s.Execute(new DoneCommand());
        Assert.Empty(all);
    }

    [Fact]
    public void Focus_on_and_off_report_minutes_and_reason()
    {
        var (s, _, t, all, _) = Make();
        s.Execute(new FocusCommand(25));
        s.Execute(new FocusOffCommand());
        s.Execute(new FocusCommand(10));
        t.Advance(TimeSpan.FromMinutes(11));
        s.Tick();

        var focus = all.Where(e => e.Name.StartsWith("deskpal.focus", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, focus.Count);
        Assert.Equal(25, focus[0].Data["minutes"]!.GetValue<int>());
        Assert.Equal("stopped", focus[1].Data["reason"]!.GetValue<string>());
        Assert.Equal(EventCause.User, focus[1].Cause);
        Assert.Equal("ended", focus[3].Data["reason"]!.GetValue<string>());
        Assert.Equal(EventCause.Schedule, focus[3].Cause);
    }

    [Fact]
    public void Pause_on_carries_until_and_pause_off_follows_resume()
    {
        var (s, _, _, all, _) = Make();
        s.Execute(new PauseCommand(30));
        s.Execute(new ResumeCommand());
        Assert.Equal(DeskPalEventNames.PauseOn, all[0].Name);
        Assert.Equal("2026-11-11T09:30:00+00:00", all[0].Data["until"]!.GetValue<string>());
        Assert.Equal(DeskPalEventNames.PauseOff, all[1].Name);
    }

    [Fact]
    public void Payload_is_camel_case_with_event_at_and_cause()
    {
        var e = new DeskPalEvent(DeskPalEventNames.FocusOn, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(1)), EventCause.User,
            new JsonObject { ["minutes"] = 25 });
        Assert.Equal("{\"event\":\"deskpal.focus.on\",\"at\":\"2026-11-12T10:00:00+01:00\",\"cause\":\"user\",\"minutes\":25}", e.ToJson());
    }

    [Fact]
    public void External_scope_tags_events_and_loop_guard_keeps_them_in()
    {
        var (s, _, _, all, published) = Make();
        using (DeskPalEventHub.External()) s.Execute(new PauseCommand(30));
        Assert.Single(all);
        Assert.Equal(EventCause.External, all[0].Cause);
        Assert.Empty(published);
        s.Execute(new ResumeCommand());
        Assert.Equal(EventCause.User, all[1].Cause);
        Assert.Single(published);
    }

    [Fact]
    public void Each_event_name_is_published_at_most_once_a_minute()
    {
        var (s, _, t, all, published) = Make();
        s.Execute(new PauseCommand(30));
        s.Execute(new ResumeCommand());
        s.Execute(new PauseCommand(30));
        Assert.Equal(3, all.Count);
        Assert.Equal(2, published.Count);
        t.Advance(TimeSpan.FromSeconds(61));
        s.Execute(new PauseCommand(40));   // renewal: no new event
        s.Execute(new ResumeCommand());
        s.Execute(new PauseCommand(30));
        Assert.Equal(DeskPalEventNames.PauseOn, published[^1].Name);
        Assert.Equal(4, published.Count);
    }

    [Fact]
    public void Goal_reached_fires_once_when_a_count_meets_its_target()
    {
        var settings = new DeskPalSettings { WorkingHours = new WorkingHours { Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) } };
        var (s, _, t, all, _) = Make(settings);
        var goals = DailyGoals.For(settings);
        Assert.Equal(2, goals.Stretches);
        for (var i = 0; i < 3; i++)
        {
            s.Execute(new BreakNowCommand(NudgeKind.Stretch));
            s.Execute(new DoneCommand());
            t.Advance(TimeSpan.FromMinutes(1));
        }
        var reached = all.Where(e => e.Name == DeskPalEventNames.GoalReached).ToList();
        Assert.Contains(reached, e => e.Data["goal"]!.GetValue<string>() == "stretches");
        Assert.Single(reached, e => e.Data["goal"]!.GetValue<string>() == "stretches");
    }

    [Fact]
    public void Day_summary_fires_once_when_working_hours_end_with_counts_only()
    {
        var (s, hub, t, all, _) = Make();
        s.Execute(new BreakNowCommand(NudgeKind.Stretch));
        s.Execute(new DoneCommand());
        hub.Tick();
        t.Advance(TimeSpan.FromHours(8.5));   // 17:30
        hub.Tick();
        t.Advance(TimeSpan.FromMinutes(5));
        hub.Tick();
        var summary = Assert.Single(all, e => e.Name == DeskPalEventNames.DaySummary);
        Assert.Equal(EventCause.Schedule, summary.Cause);
        var counts = summary.Data["counts"]!.AsObject();
        Assert.Equal(1, counts["stretches"]!.GetValue<int>());
        Assert.Equal(["stretches", "eyeBreaks", "walks"], counts.Select(p => p.Key));
    }
}

public class ControlParserTests
{
    [Theory]
    [InlineData("""{"v":1,"cmd":"ping"}""", "ping")]
    [InlineData("""{"cmd":"STATUS"}""", "status")]
    [InlineData("""{"v":1,"cmd":"resume","args":null}""", "resume")]
    [InlineData("""{"v":1,"cmd":"focus.off","args":{}}""", "focus.off")]
    public void Accepts_known_commands(string line, string cmd) => Assert.Equal(cmd, ControlParser.Parse(line).Command?.Name);

    [Theory]
    [InlineData("", ControlErrors.BadRequest)]
    [InlineData("not json", ControlErrors.BadRequest)]
    [InlineData("[1,2]", ControlErrors.BadRequest)]
    [InlineData("""{"v":1}""", ControlErrors.BadRequest)]
    [InlineData("""{"v":2,"cmd":"ping"}""", ControlErrors.UnsupportedVersion)]
    [InlineData("""{"v":"1","cmd":"ping"}""", ControlErrors.UnsupportedVersion)]
    [InlineData("""{"v":1,"cmd":"water"}""", ControlErrors.UnknownCommand)]
    [InlineData("""{"v":1,"cmd":"pause","args":[]}""", ControlErrors.BadArgs)]
    [InlineData("""{"v":1,"cmd":"pause","args":{"minutes":"lots"}}""", ControlErrors.BadArgs)]
    [InlineData("""{"v":1,"cmd":"break.start"}""", ControlErrors.BadArgs)]
    [InlineData("""{"v":1,"cmd":"break.start","args":{"kind":"breathe"}}""", ControlErrors.BadArgs)]
    public void Rejects_bad_input_with_a_code(string line, string error)
    {
        var parsed = ControlParser.Parse(line);
        Assert.Null(parsed.Command);
        Assert.Equal(error, parsed.Error);
    }

    [Theory]
    [InlineData("focus.on", null, 25)]
    [InlineData("focus.on", "1", 5)]
    [InlineData("focus.on", "500", 120)]
    [InlineData("focus.on", "44.6", 45)]
    [InlineData("pause", null, 30)]
    [InlineData("pause", "0", 5)]
    [InlineData("pause", "-20", 5)]
    [InlineData("pause", "100000", 480)]
    [InlineData("pause", "90", 90)]
    public void Clamps_minutes(string cmd, string? minutes, int expected)
    {
        var args = minutes is null ? "" : $",\"args\":{{\"minutes\":{minutes}}}";
        var parsed = ControlParser.Parse($"{{\"v\":1,\"cmd\":\"{cmd}\"{args}}}");
        Assert.Equal(expected, parsed.Command?.Minutes);
    }

    [Fact]
    public void Break_kind_is_parsed()
    {
        Assert.Equal(NudgeKind.Eyes, ControlParser.Parse("""{"v":1,"cmd":"break.start","args":{"kind":"Eyes"}}""").Command?.Kind);
    }

    [Fact]
    public void Caller_ids_prefer_package_family_and_names_are_friendly()
    {
        var packaged = new CallerInfo(1, @"C:\Program Files\WindowsApps\x\AutomationPortal.exe", "34458MindtheApp.AutomationPortal_24e29cj9741rt");
        Assert.Equal("pkg:34458MindtheApp.AutomationPortal_24e29cj9741rt", packaged.AppId);
        Assert.Equal("Automation Portal", packaged.DisplayName);
        var exe = new CallerInfo(2, @"C:\Tools\script-runner.exe", null);
        Assert.Equal(@"C:\Tools\script-runner.exe", exe.AppId);
        Assert.Equal("script-runner", exe.DisplayName);
        Assert.Null(new CallerInfo(3, null, null).AppId);
    }
}

public sealed class FakeHost : IControlHost
{
    public bool AllowExternalControl { get; set; } = true;
    public HashSet<string> Approved { get; } = [];
    public bool? Answer { get; set; } = true;
    public int Asked { get; private set; }

    public Task<T> RunOnUiAsync<T>(Func<T> work) => Task.FromResult(work());
    public bool IsApproved(string appId) => Approved.Contains(appId);

    public Task<bool?> AskApprovalAsync(CallerInfo caller, TimeSpan wait)
    {
        Asked++;
        if (Answer == true) Approved.Add(caller.AppId!);
        return Task.FromResult(Answer);
    }
}

public class ControlServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly CallerInfo Caller = new(42, @"C:\Apps\AutomationPortal.exe", null);

    private static (ControlService svc, NudgeScheduler s, FakeHost host, FakeTime t, List<DeskPalEvent> events) Make()
    {
        var t = new FakeTime(Start);
        var settings = new DeskPalSettings { AllowExternalControl = true };
        var s = new NudgeScheduler(t, settings);
        var hub = new DeskPalEventHub(s, settings, t);
        var events = new List<DeskPalEvent>();
        hub.Raised += events.Add;
        var host = new FakeHost();
        return (new ControlService(s, settings, host, t, "0.12.0", "portable"), s, host, t, events);
    }

    [Fact]
    public async Task Ping_and_status_work_even_when_control_is_off()
    {
        var (svc, _, host, _, _) = Make();
        host.AllowExternalControl = false;
        var ping = await svc.HandleAsync("""{"v":1,"cmd":"ping"}""", new CallerInfo(0, null, null));
        Assert.True(ping.Ok);
        Assert.Equal("DeskPal", ping.Result!["app"]!.GetValue<string>());
        var status = await svc.HandleAsync("""{"v":1,"cmd":"status"}""", Caller);
        Assert.True(status.Ok);
        Assert.Equal("active", status.Status!["state"]!.GetValue<string>());
        Assert.Equal(0, host.Asked);
    }

    [Fact]
    public async Task State_changes_need_the_setting()
    {
        var (svc, s, host, _, _) = Make();
        host.AllowExternalControl = false;
        var reply = await svc.HandleAsync("""{"v":1,"cmd":"pause","args":{"minutes":60}}""", Caller);
        Assert.Equal(ControlErrors.NotAllowed, reply.Error);
        Assert.Null(s.ManualHold);
    }

    [Fact]
    public async Task Approval_is_asked_once_per_app_and_denial_cools_down()
    {
        var (svc, s, host, _, _) = Make();
        Assert.True((await svc.HandleAsync("""{"v":1,"cmd":"pause"}""", Caller)).Ok);
        Assert.True((await svc.HandleAsync("""{"v":1,"cmd":"resume"}""", Caller)).Ok);
        Assert.Equal(1, host.Asked);

        var other = new CallerInfo(7, @"C:\Other\tool.exe", null);
        host.Answer = false;
        Assert.Equal(ControlErrors.Denied, (await svc.HandleAsync("""{"v":1,"cmd":"pause"}""", other)).Error);
        Assert.Equal(ControlErrors.Denied, (await svc.HandleAsync("""{"v":1,"cmd":"pause"}""", other)).Error);
        Assert.Equal(2, host.Asked);
        host.Answer = null;
        var third = new CallerInfo(8, @"C:\Other\slow.exe", null);
        Assert.Equal(ControlErrors.NeedsApproval, (await svc.HandleAsync("""{"v":1,"cmd":"pause"}""", third)).Error);
        Assert.Null(s.ManualHold);
        Assert.Equal(ControlErrors.NotAllowed, (await svc.HandleAsync("""{"v":1,"cmd":"pause"}""", new CallerInfo(0, null, null))).Error);
    }

    [Fact]
    public async Task Commands_change_state_and_raise_external_events()
    {
        var (svc, s, _, _, events) = Make();
        var focus = await svc.HandleAsync("""{"v":1,"cmd":"focus.on","args":{"minutes":45}}""", Caller);
        Assert.Equal("started", focus.Result!.GetValue<string>());
        Assert.Equal("focus", focus.Status!["state"]!.GetValue<string>());
        Assert.Equal(Start.AddMinutes(45), s.FocusHold!.Until);
        Assert.Equal("stopped", (await svc.HandleAsync("""{"v":1,"cmd":"focus.off"}""", Caller)).Result!.GetValue<string>());
        Assert.Equal("not-active", (await svc.HandleAsync("""{"v":1,"cmd":"focus.off"}""", Caller)).Result!.GetValue<string>());

        var pause = await svc.HandleAsync("""{"v":1,"cmd":"pause","args":{"minutes":1000}}""", Caller);
        Assert.Equal(Start.AddMinutes(480), s.ManualHold!.Until);
        Assert.Equal("paused", pause.Status!["state"]!.GetValue<string>());
        Assert.Equal("resumed", (await svc.HandleAsync("""{"v":1,"cmd":"resume"}""", Caller)).Result!.GetValue<string>());

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(EventCause.External, e.Cause));
    }

    [Fact]
    public async Task Break_start_is_busy_while_a_nudge_shows_and_limited_to_one_per_five_minutes()
    {
        var (svc, s, _, t, _) = Make();
        var first = await svc.HandleAsync("""{"v":1,"cmd":"break.start","args":{"kind":"stretch"}}""", Caller);
        Assert.Equal("shown", first.Result!.GetValue<string>());
        Assert.Equal(ControlErrors.RateLimited, (await svc.HandleAsync("""{"v":1,"cmd":"break.start","args":{"kind":"water"}}""", Caller)).Error);
        t.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(ControlErrors.Busy, (await svc.HandleAsync("""{"v":1,"cmd":"break.start","args":{"kind":"water"}}""", Caller)).Error);
        s.Execute(new DoneCommand());
        Assert.True((await svc.HandleAsync("""{"v":1,"cmd":"break.start","args":{"kind":"water"}}""", Caller)).Ok);
    }

    [Fact]
    public async Task Bad_input_is_answered_not_thrown()
    {
        var (svc, _, _, _, _) = Make();
        var reply = await svc.HandleAsync("{\"v\":1,\"cmd\":", Caller);
        Assert.False(reply.Ok);
        Assert.Equal(ControlErrors.BadRequest, reply.Error);
        using var doc = JsonDocument.Parse(reply.ToJson());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}

public class PipeRoundTripTests
{
    private static string TestPipe() => "DeskPalTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Control_server_answers_one_line_and_resolves_the_caller()
    {
        var name = TestPipe();
        CallerInfo? seen = null;
        using var server = new ControlPipeServer(name, (line, caller) =>
        {
            seen = caller;
            return Task.FromResult(ControlReply.Done(line.Contains("ping") ? "pong" : "other", null));
        });
        Assert.True(server.Start());

        var stream = await new NamedPipeConnector().ConnectAsync(name, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(stream);
        await using (stream)
        {
            await PipeLines.WriteLineAsync(stream, """{"v":1,"cmd":"ping"}""", TimeSpan.FromSeconds(2), CancellationToken.None);
            var (status, line) = await PipeLines.ReadLineAsync(stream, 64 * 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(PipeLines.ReadStatus.Line, status);
            Assert.Equal("""{"ok":true,"result":"pong"}""", line);
        }
        Assert.Equal(Environment.ProcessId, seen!.ProcessId);
        Assert.False(string.IsNullOrEmpty(seen.ExePath));
    }

    [Fact]
    public async Task Control_server_refuses_oversized_lines()
    {
        var name = TestPipe();
        using var server = new ControlPipeServer(name, (_, _) => Task.FromResult(ControlReply.Done("x", null)));
        Assert.True(server.Start());
        await using var stream = (await new NamedPipeConnector().ConnectAsync(name, TimeSpan.FromSeconds(2), CancellationToken.None))!;
        try { await PipeLines.WriteLineAsync(stream, new string('a', 70 * 1024), TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        var (status, line) = await PipeLines.ReadLineAsync(stream, 64 * 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(PipeLines.ReadStatus.Line, status);
        Assert.Contains(ControlErrors.TooLarge, line);
    }

    [Fact]
    public async Task Connector_returns_null_quickly_when_nobody_listens()
    {
        var started = DateTime.UtcNow;
        Assert.Null(await new NamedPipeConnector().ConnectAsync(TestPipe(), AutomationPortalClient.ConnectTimeout, CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    // A stand-in for Automation Portal's pipe: answers capabilities, records event requests.
    private sealed class FakeAutomationPortal : IDisposable
    {
        private readonly ControlPipeServer _server;
        public List<JsonElement> Requests { get; } = [];

        public FakeAutomationPortal(string name, bool supportsEvent)
        {
            _server = new ControlPipeServer(name, (line, _) =>
            {
                using var doc = JsonDocument.Parse(line);
                lock (Requests) Requests.Add(doc.RootElement.Clone());
                var kind = doc.RootElement.GetProperty("kind").GetString();
                var kinds = supportsEvent ? "[\"ping\",\"capabilities\",\"list\",\"run\",\"import\",\"event\"]" : "[\"ping\",\"capabilities\",\"list\",\"run\",\"import\"]";
                var output = kind == "capabilities"
                    ? $"{{\"app\":\"AutomationPortal\",\"version\":\"1.4.0\",\"contract\":1,\"edition\":\"store\",\"kinds\":{kinds},\"features\":[]}}"
                    : "{\"status\":\"accepted\"}";
                return Task.FromResult(new ControlReply { Ok = true, Result = JsonValue.Create(output) });
            });
            _server.Start();
        }

        public void Dispose() => _server.Dispose();
    }

    // Wraps the fake's ControlReply shape into Automation Portal's {exitCode,output} shape.
    private sealed class ApShapeConnector : IPipeConnector
    {
        private readonly NamedPipeConnector _inner = new();
        public async Task<Stream?> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            var s = await _inner.ConnectAsync(pipeName, TimeSpan.FromSeconds(2), ct);
            return s is null ? null : new ReshapingStream(s);
        }
    }

    // Turns {"ok":true,"result":"<text>"} into {"exitCode":0,"output":"<text>"} on the way back.
    private sealed class ReshapingStream(Stream inner) : Stream
    {
        private MemoryStream? _reply;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => inner.WriteAsync(b, ct);
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_reply is null)
            {
                var (_, line) = await PipeLines.ReadLineAsync(inner, 1 << 20, TimeSpan.FromSeconds(5), ct);
                var output = line is null ? null : JsonDocument.Parse(line).RootElement.GetProperty("result").GetString();
                var json = JsonSerializer.Serialize(new { exitCode = 0, output }) + "\n";
                _reply = new MemoryStream(PipeLines.Utf8.GetBytes(json));
            }
            return await _reply.ReadAsync(buffer, ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static DeskPalEvent Focus() =>
        new(DeskPalEventNames.FocusOn, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), EventCause.User, new JsonObject { ["minutes"] = 25 });

    [Fact]
    public async Task Client_sends_events_in_automation_portal_shape_when_supported()
    {
        var name = TestPipe();
        using var ap = new FakeAutomationPortal(name, supportsEvent: true);
        var client = new AutomationPortalClient(new ApShapeConnector(), name, TimeProvider.System);
        Assert.Equal(ApSendResult.Sent, await client.SendEventAsync(Focus(), CancellationToken.None));
        Assert.Equal(ApSendResult.Sent, await client.SendEventAsync(Focus(), CancellationToken.None));

        Assert.Equal(3, ap.Requests.Count);   // capabilities once (cached), then two events
        Assert.Equal("capabilities", ap.Requests[0].GetProperty("kind").GetString());
        var ev = ap.Requests[1];
        Assert.Equal("event", ev.GetProperty("kind").GetString());
        Assert.Equal("deskpal.focus.on", ev.GetProperty("name").GetString());
        Assert.Equal("deskpal", ev.GetProperty("source").GetString());
        Assert.False(ev.GetProperty("wait").GetBoolean());
        Assert.Equal(JsonValueKind.Array, ev.GetProperty("files").ValueKind);
        Assert.Contains("\"minutes\":25", ev.GetProperty("input").GetString());
    }

    [Fact]
    public async Task Client_skips_events_when_the_kind_is_missing_or_the_app_is_absent()
    {
        var name = TestPipe();
        using (var ap = new FakeAutomationPortal(name, supportsEvent: false))
        {
            var client = new AutomationPortalClient(new ApShapeConnector(), name, TimeProvider.System);
            Assert.Equal(ApSendResult.Unsupported, await client.SendEventAsync(Focus(), CancellationToken.None));
            Assert.Single(ap.Requests);
        }
        var absent = new AutomationPortalClient(new NamedPipeConnector(), TestPipe(), TimeProvider.System);
        Assert.Equal(ApSendResult.Absent, await absent.SendEventAsync(Focus(), CancellationToken.None));
    }

    [Fact]
    public async Task Forwarder_counts_skipped_events_only_while_sharing_is_on()
    {
        var enabled = false;
        var client = new AutomationPortalClient(new NamedPipeConnector(), TestPipe(), TimeProvider.System);
        using var forwarder = new AutomationPortalForwarder(client, () => enabled, TimeProvider.System);
        forwarder.Post(Focus());
        await forwarder.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, forwarder.SkippedToday);
        enabled = true;
        forwarder.Post(Focus());
        forwarder.Post(Focus());
        await forwarder.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, forwarder.SkippedToday);
    }

    [Fact]
    public void Pipe_names_follow_the_sid_and_data_root_convention()
    {
        Assert.Equal("DeskPal-S-1-5-21-1", PipeNames.DeskPal("S-1-5-21-1", null));
        var debug = PipeNames.DeskPal("S-1-5-21-1", @"C:\Users\x\AppData\Roaming\DeskPal\debug");
        Assert.Matches("^DeskPal-S-1-5-21-1-[0-9A-F]{8}$", debug);
        Assert.Equal(debug, PipeNames.DeskPal("S-1-5-21-1", @"C:\USERS\X\AppData\Roaming\DeskPal\debug\"));
        Assert.Equal("AutomationPortal-S-1-5-21-1", PipeNames.AutomationPortal("S-1-5-21-1"));
    }
}
