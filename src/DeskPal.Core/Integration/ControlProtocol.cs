using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskPal.Core.Integration;

// Desk Pal's control pipe: {"v":1,"cmd":"...","args":{...}} in, {"ok":...} out. See docs/DESKPAL-PIPE-CONTRACT.md.
public static class ControlErrors
{
    public const string BadRequest = "bad-request";
    public const string UnsupportedVersion = "unsupported-version";
    public const string UnknownCommand = "unknown-command";
    public const string BadArgs = "bad-args";
    public const string NotAllowed = "not-allowed";
    public const string NeedsApproval = "needs-approval";
    public const string Denied = "denied";
    public const string Busy = "busy";
    public const string RateLimited = "rate-limited";
    public const string TooLarge = "too-large";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
}

public static class ControlCommands
{
    public const int Contract = 1;
    public const string Ping = "ping";
    public const string Status = "status";
    public const string BreakStart = "break.start";
    public const string FocusOn = "focus.on";
    public const string FocusOff = "focus.off";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Open = "open";

    public const int FocusDefault = 25, FocusMin = 5, FocusMax = 120;
    public const int PauseDefault = 30, PauseMin = 5, PauseMax = 480;

    public static IReadOnlyList<string> All { get; } = [Ping, Status, Open, BreakStart, FocusOn, FocusOff, Pause, Resume];

    public static bool IsReadOnly(string cmd) => cmd is Ping or Status or Open;
}

public sealed record ControlCommand(string Name, NudgeKind? Kind = null, int? Minutes = null, string? Page = null, string? Section = null)
{
    public bool IsReadOnly => ControlCommands.IsReadOnly(Name);
}

public sealed record ParsedControl(ControlCommand? Command, string? Error, string? Message);

public sealed class ControlReply
{
    public bool Ok { get; init; }
    public JsonNode? Result { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public JsonObject? Status { get; init; }

    public static ControlReply Fail(string error, string? message = null) => new() { Ok = false, Error = error, Message = message };
    public static ControlReply Done(JsonNode? result, JsonObject? status) => new() { Ok = true, Result = result, Status = status };

    public string ToJson()
    {
        var o = new JsonObject { ["ok"] = Ok };
        if (Result is not null) o["result"] = Result.DeepClone();
        if (Error is not null) o["error"] = Error;
        if (Message is not null) o["message"] = Message;
        if (Status is not null) o["status"] = Status.DeepClone();
        return o.ToJsonString(DeskPalEvent.Compact);
    }
}

public static class ControlParser
{
    public static ParsedControl Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Bad(ControlErrors.BadRequest, "Empty request.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { return Bad(ControlErrors.BadRequest, "The request isn't valid JSON."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Bad(ControlErrors.BadRequest, "The request must be a JSON object.");
            if (root.TryGetProperty("v", out var v) && !(v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ver) && ver == ControlCommands.Contract))
                return Bad(ControlErrors.UnsupportedVersion, "Only contract version 1 is supported.");
            if (!root.TryGetProperty("cmd", out var c) || c.ValueKind != JsonValueKind.String)
                return Bad(ControlErrors.BadRequest, "The request has no \"cmd\".");
            var cmd = c.GetString()!.Trim().ToLowerInvariant();
            JsonElement args = default;
            var hasArgs = root.TryGetProperty("args", out args) && args.ValueKind != JsonValueKind.Null;
            if (hasArgs && args.ValueKind != JsonValueKind.Object) return Bad(ControlErrors.BadArgs, "\"args\" must be an object.");

            switch (cmd)
            {
                case ControlCommands.Ping:
                case ControlCommands.Status:
                case ControlCommands.FocusOff:
                case ControlCommands.Resume:
                    return Ok(new ControlCommand(cmd));
                case ControlCommands.Open:
                {
                    string? page = null, section = null;
                    if (hasArgs)
                    {
                        if (args.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.String) page = pg.GetString();
                        if (args.TryGetProperty("section", out var sc) && sc.ValueKind == JsonValueKind.String) section = sc.GetString();
                    }
                    var norm = DeskPalLink.NormalizePage(page);
                    return Ok(new ControlCommand(cmd, Page: norm, Section: DeskPalLink.NormalizeSection(norm, section)));
                }
                case ControlCommands.BreakStart:
                {
                    if (!hasArgs || !args.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
                        return Bad(ControlErrors.BadArgs, "break.start needs \"kind\": stretch, water, eyes or walk.");
                    NudgeKind? kind = k.GetString()!.Trim().ToLowerInvariant() switch
                    {
                        "stretch" => NudgeKind.Stretch,
                        "water" => NudgeKind.Water,
                        "eyes" => NudgeKind.Eyes,
                        "walk" => NudgeKind.Walk,
                        _ => null,
                    };
                    return kind is null
                        ? Bad(ControlErrors.BadArgs, "\"kind\" must be stretch, water, eyes or walk.")
                        : Ok(new ControlCommand(cmd, Kind: kind));
                }
                case ControlCommands.FocusOn:
                    return Minutes(cmd, hasArgs ? args : default, ControlCommands.FocusDefault, ControlCommands.FocusMin, ControlCommands.FocusMax);
                case ControlCommands.Pause:
                    return Minutes(cmd, hasArgs ? args : default, ControlCommands.PauseDefault, ControlCommands.PauseMin, ControlCommands.PauseMax);
                default:
                    return Bad(ControlErrors.UnknownCommand, "Desk Pal doesn't know that command.");
            }
        }
    }

    // A missing "minutes" takes the default; a number is rounded and clamped into range; anything else is refused.
    private static ParsedControl Minutes(string cmd, JsonElement args, int fallback, int min, int max)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("minutes", out var m) || m.ValueKind == JsonValueKind.Null)
            return Ok(new ControlCommand(cmd, Minutes: fallback));
        if (m.ValueKind != JsonValueKind.Number || !m.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d))
            return Bad(ControlErrors.BadArgs, "\"minutes\" must be a number.");
        var clamped = (int)Math.Clamp(Math.Round(d), min, max);
        return Ok(new ControlCommand(cmd, Minutes: clamped));
    }

    private static ParsedControl Ok(ControlCommand c) => new(c, null, null);
    private static ParsedControl Bad(string error, string message) => new(null, error, message);
}

// Who is on the other end of the pipe. AppId is what approvals are remembered by.
public sealed record CallerInfo(int ProcessId, string? ExePath, string? PackageFamily)
{
    public const string AutomationPortalPackageFamily = "34458MindtheApp.AutomationPortal_24e29cj9741rt";

    public string? AppId => PackageFamily is { Length: > 0 } f ? "pkg:" + f : ExePath is { Length: > 0 } p ? p : null;

    public string DisplayName => DisplayNameFor(AppId);

    public static string DisplayNameFor(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "An unknown app";
        // Only the Store package is named as Automation Portal; any exe could call itself that, so exes show their file name.
        if (string.Equals(appId, "pkg:" + AutomationPortalPackageFamily, StringComparison.OrdinalIgnoreCase)) return "Automation Portal";
        if (appId.StartsWith("pkg:", StringComparison.Ordinal))
        {
            var family = appId[4..];
            var name = family.Split('_')[0];
            var dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        }
        return Path.GetFileNameWithoutExtension(appId);
    }
}
