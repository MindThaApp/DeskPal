namespace DeskPal.Core;

public enum RevertAction { None, AskToKeep, RevertNow }

public sealed record RevertDecision(RevertAction Action, TimeSpan Remaining);

// After a language switch the user has 30 seconds to confirm it. The switch is stored as the language to go back to
// plus the time of the switch, so a crash, a closed window or a slow start all end in the previous language.
// The keep-this-language prompt wording in one language. Countdown is a template with {0} for the seconds.
public sealed class LanguagePromptText
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Countdown { get; set; } = "";
    public string Keep { get; set; } = "";
    public string GoBack { get; set; } = "";

    public string CountdownFor(int seconds) => Countdown.Replace("{0}", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public static class LanguageRevert
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    public static void Begin(DeskPalSettings settings, string newTag, DateTimeOffset now, LanguagePromptText? previousText = null)
    {
        settings.PendingLanguageRevert = settings.Language ?? "";
        settings.PendingLanguageRevertText = previousText;
        settings.PendingLanguageRevertAt = now;
        settings.Language = newTag;
    }

    // Keep: the new language stays.
    public static void Confirm(DeskPalSettings settings)
    {
        settings.PendingLanguageRevert = null;
        settings.PendingLanguageRevertAt = null;
        settings.PendingLanguageRevertText = null;
    }

    // Go back (or time out): the previous language returns.
    public static void Revert(DeskPalSettings settings)
    {
        if (settings.PendingLanguageRevert is { } previous) settings.Language = previous;
        Confirm(settings);
    }

    // At startup, and while the prompt counts down. A pending switch older than the window, with no timestamp, or
    // stamped in the future (clock change) is reverted.
    public static RevertDecision Evaluate(DeskPalSettings settings, DateTimeOffset now)
    {
        if (settings.PendingLanguageRevert is null) return new(RevertAction.None, TimeSpan.Zero);
        if (settings.PendingLanguageRevertAt is not { } at) return new(RevertAction.RevertNow, TimeSpan.Zero);
        var elapsed = now - at;
        if (elapsed < TimeSpan.Zero || elapsed >= Window) return new(RevertAction.RevertNow, TimeSpan.Zero);
        return new(RevertAction.AskToKeep, Window - elapsed);
    }
}
