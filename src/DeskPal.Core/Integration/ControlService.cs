using System.Text.Json.Nodes;

namespace DeskPal.Core.Integration;

// What the control service needs from the app: the UI thread, the setting, and the confirm card.
public interface IControlHost
{
    bool AllowExternalControl { get; }

    // Runs on the UI thread and returns its result. Everything that reads settings or changes state goes through here.
    Task<T> RunOnUiAsync<T>(Func<T> work);

    // Called on the UI thread.
    bool IsApproved(string appId);

    // Shows the confirm-once card. True: allowed (and remembered by the host); false: "Not now"; null: no answer in time.
    Task<bool?> AskApprovalAsync(CallerInfo caller, TimeSpan wait);
}

// Handles one control request: validation, the allow setting, confirm-once per app, rate limits, then the scheduler.
public sealed class ControlService
{
    public static readonly TimeSpan ApprovalWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DeniedCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BreakGap = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    public const int MaxChangesPerWindow = 20;

    private readonly NudgeScheduler _scheduler;
    private readonly DeskPalSettings _settings;
    private readonly IControlHost _host;
    private readonly TimeProvider _time;
    private readonly string _version;
    private readonly string _edition;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> _changes = [];
    private DateTimeOffset? _lastBreak;

    public ControlService(NudgeScheduler scheduler, DeskPalSettings settings, IControlHost host, TimeProvider time, string version, string edition)
    {
        _scheduler = scheduler;
        _settings = settings;
        _host = host;
        _time = time;
        _version = version;
        _edition = edition;
    }

    public async Task<ControlReply> HandleAsync(string? line, CallerInfo caller)
    {
        var parsed = ControlParser.Parse(line);
        if (parsed.Command is not { } cmd) return ControlReply.Fail(parsed.Error ?? ControlErrors.BadRequest, parsed.Message);

        if (cmd.Name == ControlCommands.Ping)
        {
            var status = await _host.RunOnUiAsync(BuildStatus).ConfigureAwait(false);
            return ControlReply.Done(new JsonObject { ["app"] = "DeskPal", ["version"] = _version, ["contract"] = ControlCommands.Contract }, status);
        }
        if (cmd.Name == ControlCommands.Status)
            return ControlReply.Done("status", await _host.RunOnUiAsync(BuildStatus).ConfigureAwait(false));

        if (!_host.AllowExternalControl)
            return ControlReply.Fail(ControlErrors.NotAllowed, "Turn on \"Allow Automation Portal and other apps to control Desk Pal\" in Desk Pal Settings.");
        if (caller.AppId is not { } appId)
            return ControlReply.Fail(ControlErrors.NotAllowed, "Desk Pal couldn't tell which app sent this.");

        var approved = await _host.RunOnUiAsync(() => _host.IsApproved(appId)).ConfigureAwait(false);
        if (!approved)
        {
            lock (_lock)
                if (_denied.TryGetValue(appId, out var at) && _time.GetUtcNow() - at < DeniedCooldown)
                    return ControlReply.Fail(ControlErrors.Denied, "The user chose not to allow this app for now.");
            var answer = await _host.AskApprovalAsync(caller, ApprovalWait).ConfigureAwait(false);
            if (answer == false)
            {
                lock (_lock) _denied[appId] = _time.GetUtcNow();
                return ControlReply.Fail(ControlErrors.Denied, "The user chose not to allow this app for now.");
            }
            if (answer is null) return ControlReply.Fail(ControlErrors.NeedsApproval, "Waiting for the user to allow this app in Desk Pal. Try again after they answer.");
        }

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _changes.RemoveAll(t => now - t >= RateWindow);
            if (_changes.Count >= MaxChangesPerWindow) return ControlReply.Fail(ControlErrors.RateLimited, "Too many commands. Try again in a minute.");
            if (cmd.Name == ControlCommands.BreakStart && _lastBreak is { } lb && now - lb < BreakGap)
                return ControlReply.Fail(ControlErrors.RateLimited, "A break was started from outside less than 5 minutes ago.");
            _changes.Add(now);
        }

        var (result, error) = await _host.RunOnUiAsync(() => Execute(cmd)).ConfigureAwait(false);
        if (error is not null) return ControlReply.Fail(error, error == ControlErrors.Busy ? "A nudge is already showing." : null);
        if (cmd.Name == ControlCommands.BreakStart) lock (_lock) _lastBreak = _time.GetUtcNow();
        return ControlReply.Done(result, await _host.RunOnUiAsync(BuildStatus).ConfigureAwait(false));
    }

    // Runs on the UI thread. Every event the scheduler raises from here is tagged cause "external".
    public (string? Result, string? Error) Execute(ControlCommand cmd)
    {
        using var _ = DeskPalEventHub.External();
        switch (cmd.Name)
        {
            case ControlCommands.BreakStart:
                if (_scheduler.Current is not null) return (null, ControlErrors.Busy);
                return _scheduler.Execute(new BreakNowCommand(cmd.Kind!.Value)) ? ("shown", null) : (null, ControlErrors.Busy);
            case ControlCommands.FocusOn:
            {
                var renewed = _scheduler.FocusHold is not null;
                _scheduler.Execute(new FocusCommand(cmd.Minutes ?? ControlCommands.FocusDefault));
                return (renewed ? "renewed" : "started", null);
            }
            case ControlCommands.FocusOff:
                return (_scheduler.Execute(new FocusOffCommand()) ? "stopped" : "not-active", null);
            case ControlCommands.Pause:
                _scheduler.Execute(new PauseCommand(cmd.Minutes ?? ControlCommands.PauseDefault));
                return ("held", null);
            case ControlCommands.Resume:
                return (_scheduler.Execute(new ResumeCommand()) ? "resumed" : "not-paused", null);
            default:
                return (null, ControlErrors.UnknownCommand);
        }
    }

    public JsonObject BuildStatus()
    {
        var now = _time.GetLocalNow();
        var holds = _scheduler.Holds;
        var focus = holds.FirstOrDefault(h => h.Source == NudgeScheduler.FocusSource);
        var pause = holds.FirstOrDefault(h => h.Source == NudgeScheduler.ManualSource);
        string state;
        if (!_scheduler.IsWorkingTime(now)) state = "offHours";
        else if (holds.Any(h => h.Source is "dnd" or NudgeScheduler.CallSource)) state = "dnd";
        else if (focus is not null) state = "focus";
        else if (pause is not null || holds.Any(h => h.Source == NudgeScheduler.SnoozeSource)) state = "paused";
        else if (_scheduler.Current is not null) state = "break";
        else state = "active";

        var holdList = new JsonArray();
        foreach (var h in holds)
            holdList.Add(new JsonObject { ["source"] = h.Source, ["until"] = DeskPalEvent.FormatTime(h.Until), ["seated"] = h.Seated });

        var status = new JsonObject
        {
            ["contract"] = ControlCommands.Contract,
            ["app"] = "DeskPal",
            ["version"] = _version,
            ["edition"] = _edition,
            ["allowExternalControl"] = _settings.AllowExternalControl,
            ["state"] = state,
            ["holds"] = holdList,
            ["focus"] = focus is null
                ? new JsonObject { ["active"] = false }
                : new JsonObject { ["active"] = true, ["endsAt"] = DeskPalEvent.FormatTime(focus.Until) },
        };
        if (pause is not null) status["pausedUntil"] = DeskPalEvent.FormatTime(pause.Until);
        if (_scheduler.NextDue is { } next && DeskPalEventHub.KindText(next.Kind) is { } kind)
            status["nextNudge"] = new JsonObject { ["kind"] = kind, ["dueAt"] = DeskPalEvent.FormatTime(next.At) };
        var goals = DailyGoals.For(_settings);
        status["today"] = new JsonObject
        {
            ["stretches"] = _scheduler.StretchesToday,
            ["eyeBreaks"] = _scheduler.EyeBreaksToday,
            ["walks"] = _scheduler.WalksToday,
        };
        status["goals"] = new JsonObject { ["stretches"] = goals.Stretches, ["eyeBreaks"] = goals.EyeBreaks, ["walks"] = goals.Walks };
        return status;
    }
}
