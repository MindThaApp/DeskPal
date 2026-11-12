using DeskPal.Core.Integration;
using Microsoft.Win32;

namespace DeskPal;

// The deskpal: link scheme: per-user registration for the portable build (the Store package declares it in its manifest) and hand-off to a running copy.
public static class DeskPalProtocol
{
    private const string KeyPath = @"Software\Classes\deskpal";

    // Real data root only. Writes HKCU\Software\Classes\deskpal (and its DefaultIcon and shell\open\command subkeys) when the key
    // is absent, or when its command points to a DeskPal.exe that no longer exists. Never replaces a working registration, never deletes.
    public static void RegisterInBackground()
    {
        // The Store package declares the scheme in its manifest; a packaged copy writes nothing to the registry.
        if (AppData.IsDebugRun || PackagedApp.IsPackaged) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        _ = Task.Run(() =>
        {
            try { Register(exe); }
            catch (Exception ex) { Logger.Log("deskpal: registration failed: " + ex.Message); }
        });
    }

    private static void Register(string exe)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath))
        {
            if (existing is not null)
            {
                string? command = null;
                using (var c = existing.OpenSubKey(@"shell\open\command")) command = c?.GetValue(null) as string;
                var target = ExePathOf(command);
                var stale = target is not null
                    && string.Equals(Path.GetFileName(target), "DeskPal.exe", StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(target);
                if (!stale) return;
                Logger.Log("deskpal: replacing a stale registration");
            }
        }
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(null, "URL:Desk Pal");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, "\"" + exe + "\",0");
        using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, "\"" + exe + "\" \"%1\"");
        Logger.Log("deskpal: registered link scheme");
    }

    public static string? ExePathOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var sp = c.IndexOf(' ');
        return sp > 0 ? c[..sp] : c;
    }

    // Second launch: ask the running copy to show the page over its control pipe ("open" needs no permission). True when it answered.
    public static bool ForwardToRunning(DeskPalLink link)
    {
        try
        {
            var name = PipeNames.DeskPal(PipeNames.UserSid, AppData.IsDebugRun ? AppData.Dir : null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return Task.Run(async () =>
            {
                await using var stream = await new NamedPipeConnector().ConnectAsync(name, TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
                if (stream is null) return false;
                var args = new System.Text.Json.Nodes.JsonObject { ["page"] = link.Page };
                if (link.Section is not null) args["section"] = link.Section;
                var req = new System.Text.Json.Nodes.JsonObject { ["v"] = 1, ["cmd"] = ControlCommands.Open, ["args"] = args };
                await PipeLines.WriteLineAsync(stream, req.ToJsonString(), TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
                var (status, _) = await PipeLines.ReadLineAsync(stream, 64 * 1024, TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
                return status == PipeLines.ReadStatus.Line;
            }, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Log("deskpal: forward failed: " + ex.Message);
            return false;
        }
    }
}
