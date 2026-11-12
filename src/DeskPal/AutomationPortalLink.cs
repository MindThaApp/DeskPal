using DeskPal.Core;
using DeskPal.Core.Integration;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace DeskPal;

// Wires Desk Pal to Automation Portal: shares events (when the user turned it on) and serves the DeskPal-<SID> control pipe.
// All UI and settings work is marshalled to the dispatcher; pipe and send work stays on background threads.
public sealed class AutomationPortalLink : IControlHost, IDisposable
{
    private readonly DispatcherQueue _dq;
    private readonly SettingsHub _hub;
    private readonly DeskPalEventHub _events;
    private readonly AutomationPortalForwarder _forwarder;
    private readonly ControlService _control;
    private readonly ControlPipeServer _server;
    private readonly Dictionary<string, TaskCompletionSource<bool?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<CallerInfo> _askQueue = new();
    private ApprovalCardWindow? _card;
    private string? _asking;

    public AutomationPortalLink(DispatcherQueue dq, SettingsHub hub, NudgeScheduler scheduler)
    {
        _dq = dq;
        _hub = hub;
        var settings = hub.Settings;
        var sid = PipeNames.UserSid;
        _events = new DeskPalEventHub(scheduler, settings, TimeProvider.System);
        var client = new AutomationPortalClient(new NamedPipeConnector(), PipeNames.AutomationPortal(sid), TimeProvider.System);
        _forwarder = new AutomationPortalForwarder(client, () => settings.ShareEventsWithAutomationPortal, TimeProvider.System, Logger.Log);
        _forwarder.SkippedChanged += () => _dq.TryEnqueue(() => TickGuard.Run("ap-skipped", _hub.RaiseChanged));
        _events.Raised += e => Logger.Log($"event {e.Name} ({DeskPalEvent.CauseText(e.Cause)})");
        _events.Published += _forwarder.Post;

        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        _control = new ControlService(scheduler, settings, this, TimeProvider.System, version, "portable");
        _server = new ControlPipeServer(PipeNames.DeskPal(sid, AppData.IsDebugRun ? AppData.Dir : null), _control.HandleAsync, OnBackgroundError);
    }

    public int EventsSkippedToday => _forwarder.SkippedToday;

    public void Start()
    {
        if (_server.Start()) Logger.Log("control pipe listening");
        else Logger.Log("control pipe not started");
    }

    // Call on every scheduler tick (UI thread).
    public void Tick() => _events.Tick();

    public bool AllowExternalControl => _hub.Settings.AllowExternalControl;

    public Task<T> RunOnUiAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dq.TryEnqueue(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!queued) tcs.SetException(new InvalidOperationException("The UI thread is gone."));
        return tcs.Task;
    }

    public bool IsApproved(string appId) =>
        _hub.Settings.ApprovedControlApps.Contains(appId, StringComparer.OrdinalIgnoreCase);

    public async Task<bool?> AskApprovalAsync(CallerInfo caller, TimeSpan wait)
    {
        var task = await RunOnUiAsync(() => Enqueue(caller)).ConfigureAwait(false);
        var done = await Task.WhenAny(task, Task.Delay(wait)).ConfigureAwait(false);
        return done == task ? await task.ConfigureAwait(false) : null;
    }

    // UI thread. One card at a time; a second request from the same app shares the first one's answer.
    private Task<bool?> Enqueue(CallerInfo caller)
    {
        var id = caller.AppId!;
        if (_pending.TryGetValue(id, out var existing)) return existing.Task;
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _askQueue.Enqueue(caller);
        if (_asking is null) AskNext();
        return tcs.Task;
    }

    private void AskNext()
    {
        if (_askQueue.Count == 0) { _asking = null; return; }
        var caller = _askQueue.Dequeue();
        _asking = caller.AppId;
        try
        {
            if (_card is null)
            {
                _card = new ApprovalCardWindow();
                _card.Answered += OnAnswered;
            }
            Logger.Log("control approval asked");
            _card.Ask(caller.DisplayName, caller.PackageFamily ?? caller.ExePath);
        }
        catch (Exception ex)
        {
            Logger.Log("approval card failed: " + ex.Message);
            Resolve(caller.AppId!, null);
            AskNext();
        }
    }

    private void OnAnswered(bool allow)
    {
        var id = _asking;
        if (id is null) return;
        Logger.Log("control approval " + (allow ? "allowed" : "not now"));
        if (allow && !IsApproved(id))
        {
            var apps = _hub.Settings.ApprovedControlApps;
            if (apps.Count >= DeskPalSettings.MaxApprovedApps) apps.RemoveAt(0);
            apps.Add(id);
            _hub.Commit(false);
        }
        Resolve(id, allow);
        AskNext();
    }

    private void Resolve(string id, bool? answer)
    {
        if (_pending.Remove(id, out var tcs)) tcs.TrySetResult(answer);
    }

    private static void OnBackgroundError(Exception ex, string where)
    {
        try
        {
            Logger.Log($"{where}: {ex.GetType().Name}: {ex.Message}");
            if (ex is not (IOException or UnauthorizedAccessException)) CrashReporting.CaptureCrash(ex, where, handled: true);
        }
        catch { }
    }

    public void Dispose()
    {
        _server.Dispose();
        _forwarder.Dispose();
        foreach (var tcs in _pending.Values) tcs.TrySetResult(null);
        _pending.Clear();
        _card?.Quit();
    }

    // ---- links that lead to Automation Portal (owner rule in docs/INTEGRATION-AUTOMATION-PORTAL.md) ----

    public const string PackageFamily = CallerInfo.AutomationPortalPackageFamily;
    private const string StoreLink = "ms-windows-store://pdp/?ProductId=9PB9C8FHG105";
    private const string WebLink = "https://apps.microsoft.com/detail/9PB9C8FHG105";

    public static bool IsPackageInstalled()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            return manager.FindPackagesForUser(string.Empty, PackageFamily).Any();
        }
        catch { return false; }
    }

    public static bool IsProtocolRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("automationportal");
            return key?.GetValue("URL Protocol") is not null;
        }
        catch { return false; }
    }

    public static bool IsInstalled() => IsPackageInstalled() || IsProtocolRegistered();

    // Only ever called from a user's click.
    public static void OpenOrGet()
    {
        if (IsPackageInstalled()) { Shell($@"shell:AppsFolder\{PackageFamily}!App"); return; }
        if (IsProtocolRegistered()) { Shell("automationportal:"); return; }
        if (!Shell(StoreLink)) Shell(WebLink);
    }

    private static bool Shell(string target)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log("open link failed: " + ex.GetType().Name);
            return false;
        }
    }
}
