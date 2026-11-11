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
    public static readonly bool IsDebugRun = DebugEyeCue || DebugNudge || DebugCrash || DebugTray || Debug3D || DebugPoke;

    // Debug runs keep their own state and log so they never touch the real ones.
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPal", IsDebugRun ? "debug" : "");
    public static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
    public static readonly string StatePath = Path.Combine(Dir, "state.json");

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

