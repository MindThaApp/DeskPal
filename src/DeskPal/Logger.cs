namespace DeskPal;

public static class AppData
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPal");
    public static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
    public static readonly string StatePath = Path.Combine(Dir, "state.json");
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
