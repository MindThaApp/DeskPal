using Microsoft.UI.Dispatching;

namespace DeskPal;

// Every dispatcher timer callback goes through here: an exception escaping one kills the process natively,
// before any unhandled-exception event fires.
public static class TickGuard
{
    public static void Run(string name, Action body)
    {
        try { body(); }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"CRASH tick {name}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
                CrashReporting.CaptureCrash(ex, "TickGuard." + name, handled: true);
            }
            catch { }
        }
    }
}
