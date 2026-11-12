namespace DeskPal;

public static class AppData
{
    public static readonly bool DebugEyeCue = HasArg("--debug-eyecue");
    public static readonly bool DebugNudge = HasArg("--debug-nudge");
    public static readonly bool DebugCrash = HasArg("--debug-crash");
    public static readonly bool DebugTray = HasArg("--debug-tray");
    public static readonly bool Debug3DFail = HasArg("--debug-3d-fail");
    public static readonly bool Debug3D = HasArg("--debug-3d") || Debug3DFail;
    public static readonly bool DebugPoke = HasArg("--debug-poke");
    public static readonly bool Debug3DDump = HasArg("--debug-3d-dump");
    public static readonly bool DebugSettings = HasArg("--debug-settings");
    public static readonly bool DebugMain = HasArg("--debug-main");
    public static readonly bool DebugQuickPanel = HasArg("--debug-quickpanel");
    public static readonly bool DebugTrayMenu = HasArg("--debug-traymenu");
    // With --debug-quickpanel: drag Bloop away through the drag code (no real input), and optionally close the
    // panel while it is away.
    public static readonly bool DebugBloopDrag = HasArg("--debug-bloop-drag");
    public static readonly bool DebugBloopExit = HasArg("--debug-bloop-exit");
    // --debug-gesture=<region>:<gesture>[,...] (or "all"): synthetic gestures fed to the Home Bloop's own touch handler.
    public static readonly string? DebugGesture = ArgValue("--debug-gesture=");
    public static readonly bool IsDebugRun = DebugSettings || DebugMain || DebugEyeCue || DebugNudge || DebugCrash || DebugTray || DebugQuickPanel || DebugTrayMenu || Debug3D || DebugPoke || DebugGesture is not null;

    // Debug runs keep their own state and log so they never touch the real ones.
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPal", IsDebugRun ? "debug" : "");
    public static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
    public static readonly string StatePath = Path.Combine(Dir, "state.json");

    private static string? ArgValue(string prefix) =>
        Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static bool HasArg(string name) =>
        Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}

public static class Logger
{
    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        CrashReporting.Breadcrumb(message);
        try
        {
            var dir = Path.Combine(AppData.Dir, "logs");
            Directory.CreateDirectory(dir);
            lock (LogLock)
                File.AppendAllText(Path.Combine(dir, "deskpal.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}

