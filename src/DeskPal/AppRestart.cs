using System.Diagnostics;
using Microsoft.Windows.AppLifecycle;

namespace DeskPal;

// Relaunch for a language change. The new copy is told which process to wait for, so the single-instance mutex is free
// by the time it asks for it.
internal static class AppRestart
{
    public const string LanguageSwitchedArg = "--language-switched";
    public const string WizardResumeArg = "--wizard-resume";
    private const string WaitPidPrefix = "--wait-pid=";

    public static bool HasArg(string arg) =>
        Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));

    private static int? PreviousPid()
    {
        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => a.StartsWith(WaitPidPrefix, StringComparison.OrdinalIgnoreCase));
        return arg is not null && int.TryParse(arg[WaitPidPrefix.Length..], out var pid) ? pid : null;
    }

    // Takes the instance mutex. A relaunched copy retries for a few seconds while the old one finishes closing.
    public static Mutex AcquireMutex(string name, out bool created)
    {
        var mutex = new Mutex(true, name, out created);
        if (created || PreviousPid() is not { } pid) return mutex;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(10000);
        }
        catch (Exception) { /* already gone */ }
        for (var i = 0; i < 20 && !created; i++)
        {
            mutex.Dispose();
            Thread.Sleep(250);
            mutex = new Mutex(true, name, out created);
        }
        return mutex;
    }

    // Starts a new copy. Packaged builds go through the app model; the portable build starts its own exe.
    public static bool Launch(params string[] args)
    {
        // Debug runs keep their --debug-* flags so the new copy stays on the same (debug) data root.
        var debug = Environment.GetCommandLineArgs().Skip(1).Where(a => a.StartsWith("--debug-", StringComparison.OrdinalIgnoreCase));
        var all = debug.Concat(args).Append(WaitPidPrefix + Environment.ProcessId).ToArray();
        if (PackagedApp.IsPackaged)
        {
            try
            {
                var reason = AppInstance.Restart(string.Join(' ', all));
                Logger.Log("app restart returned " + reason);
            }
            catch (Exception ex) { Logger.Log("app restart failed: " + ex.Message); }
        }
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var a in all) info.ArgumentList.Add(a);
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log("restart failed: " + ex.Message);
            return false;
        }
    }
}
