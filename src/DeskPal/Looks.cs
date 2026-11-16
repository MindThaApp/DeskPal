using DeskPal.Core;

namespace DeskPal;

// Bloop and Sprout customisation for the UI. Every place that draws Bloop or Sprout reads Looks.Current and
// redraws on Looks.Changed. Current is the saved look while Pro is unlocked and the default look otherwise
// (Pro.Pick); the saved choices are never cleared, so they come back when Pro does.
internal static class Looks
{
    private static SettingsHub? _hub;
    private static CharacterLook _last = CharacterLook.Default;

    // Raised on the UI thread when the look on screen changes (a new choice, or Pro starting or ending).
    public static event Action? Changed;

    public static CharacterLook Saved => _hub?.Settings.Look.ToLook() ?? CharacterLook.Default;

    public static CharacterLook Current => Pro.Pick(Saved, CharacterLook.Default);

    public static void Attach(SettingsHub hub)
    {
        if (_hub is not null) _hub.Changed -= Check;
        _hub = hub;
        _last = Current;
        hub.Changed += Check;
    }

    // Saves one choice. The caller checks Pro first; this only writes the settings file through the hub.
    public static void Save(Action<LookSettings> change)
    {
        if (_hub is null) return;
        change(_hub.Settings.Look);
        _hub.Commit(false);
    }

    private static void Check()
    {
        var now = Current;
        if (now == _last) return;
        _last = now;
        Logger.Log($"look changed to {now.Key}");
        Changed?.Invoke();
    }
}
