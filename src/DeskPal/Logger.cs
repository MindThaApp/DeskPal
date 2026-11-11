namespace DeskPal;

public static class AppData
{
    public static readonly bool DebugEyeCue = HasArg("--debug-eyecue");
    public static readonly bool DebugNudge = HasArg("--debug-nudge");
    public static readonly bool IsDebugRun = DebugEyeCue || DebugNudge;

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
