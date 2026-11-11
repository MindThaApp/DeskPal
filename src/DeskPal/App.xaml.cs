using System.Drawing;
using System.Windows.Input;
using System.Security.Principal;
using DeskPal.Core;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DeskPal;

public partial class App : Application
{
    private const string ToastTag = "nudge";
    private static Mutex? _instanceMutex;

    private DispatcherQueue _dq = null!;
    private DeskPalSettings _settings = new();
    private NudgeScheduler _scheduler = null!;
    private ICommandRouter _router = null!;
    private readonly IBusyStateProvider _busy = new ShellBusyStateProvider();
    private TaskbarIcon? _tray;
    private NudgeCardWindow? _card;
    private EyeCuePillWindow? _pill;
    private MainWindow? _main;
    private MainWindowUi _ui = new();
    private SettingsHub _hub = null!;
    private SettingsWindow? _settingsWindow;
    private CrashReportingMode _appliedCrashMode;
    private CallMonitor _calls = null!;
    private Icon? _baseIcon;
    private TrayIcons? _icons;
    private TrayAnimator? _anim;
    private int _walkNudges;
    private DispatcherQueueTimer? _debugTrayTimer;
    private DispatcherQueueTimer? _clickTimer;
    private DispatcherQueueTimer? _debugPanelTimer;
    private QuickPanelWindow? _quick;
    private TrayMenuWindow? _menu;
    private long _lastDoubleClickTick;
    private DispatcherQueueTimer? _toastTimer;
    private Guid? _toastNudgeId;
    private DispatcherQueueTimer? _tickTimer;
    private DispatcherQueueTimer? _busyTimer;
    private DispatcherQueueTimer? _debugEyeTimer;
    private DispatcherQueueTimer? _debugNudgeTimer;
    private DispatcherQueueTimer? _debugCrashTimer;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("domain unhandled (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception,
                source: "AppDomain.UnhandledException", handled: !e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("unobserved task", e.Exception, source: "TaskScheduler.UnobservedTaskException", handled: true);
            e.SetObserved();
        };

        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var mutexName = "DeskPal-" + sid + (AppData.DebugSettings ? "-debug-settings" : AppData.DebugMain ? "-debug-main" : AppData.DebugQuickPanel ? "-debug-quickpanel" : AppData.DebugTrayMenu ? "-debug-traymenu" : AppData.IsDebugRun ? "-debug" : "");
        _instanceMutex = new Mutex(true, mutexName, out var created);
        if (!created) Environment.Exit(0);
        InitializeComponent();
        UnhandledException += OnUiUnhandledException;
    }

    private static void OnUiUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // A throwing UI event handler should not take the tray app down; corrupt-state exceptions still crash.
        var handled = e.Exception is not (OutOfMemoryException or AccessViolationException
            or System.Runtime.InteropServices.SEHException or InvalidProgramException);
        LogCrash("ui unhandled", e.Exception, e.Message, "Application.UnhandledException", handled);
        e.Handled = handled;
    }

    private static void LogCrash(string kind, Exception? ex, string? message = null, string? source = null, bool handled = true)
    {
        if (ex is null)
        {
            Logger.Log($"CRASH {kind}: {message}");
            return;
        }
        Logger.Log($"CRASH {kind}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
        if (source is not null) CrashReporting.CaptureCrash(ex, source, handled);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        _settings = JsonFileStore.Load<DeskPalSettings>(AppData.SettingsPath, TimeProvider.System);
        if (_settings.Migrate())
        {
            try { JsonFileStore.Save(AppData.SettingsPath, _settings); }
            catch (Exception ex) { Logger.Log("settings save failed: " + ex.Message); }
        }
        // The pipeline check always sends: it only runs in the debug data root, with a test exception.
        CrashReporting.Apply(AppData.DebugCrash ? CrashReportingMode.Always : _settings.CrashReports);
        var state = JsonFileStore.Load<DeskPalState>(AppData.StatePath, TimeProvider.System);
        _ui = state.Ui ??= new MainWindowUi();
        _scheduler = new NudgeScheduler(TimeProvider.System, _settings, state);
        _router = _scheduler;
        _hub = new SettingsHub(_settings, _scheduler);
        _appliedCrashMode = _settings.CrashReports;
        _hub.Changed += OnSettingsChanged;
        _calls = new CallMonitor(new RegistryCallDetector(), _scheduler, TimeProvider.System);
        _scheduler.Event += e => _dq.TryEnqueue(() => OnSchedulerEvent(e));

        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnNotificationInvoked;
        manager.Register();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "deskpal.ico");
        _baseIcon = new Icon(iconPath);
        _tray = new TaskbarIcon
        {
            ToolTipText = "Desk Pal",
            Icon = _baseIcon,
            NoLeftClickDelay = true,
            LeftClickCommand = new ActionCommand(() => _dq.TryEnqueue(() => TickGuard.Run("tray-left-click", OnTrayLeftClick))),
            DoubleClickCommand = new ActionCommand(() => _dq.TryEnqueue(() => TickGuard.Run("tray-double-click", OnTrayDoubleClick))),
            RightClickCommand = new ActionCommand(() => _dq.TryEnqueue(() => TickGuard.Run("tray-right-click", OnTrayRightClick))),
        };
        _tray.ForceCreate();

        _icons = new TrayIcons();
        _anim = new TrayAnimator(_dq, _tray, _icons,
            () => _settings.AnimateTray,
            () => !_scheduler.IsWorkingTime(TimeProvider.System.GetLocalNow()) || IsPausedUntilTomorrow(),
            () => _scheduler.Holds.Any(h => h.Source == "dnd"));
        _anim.Refresh();

        _tickTimer = _dq.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(5);
        _tickTimer.Tick += (_, _) => TickGuard.Run("scheduler", () => { _scheduler.Tick(); UpdateTray(); });
        _tickTimer.Start();

        _busyTimer = _dq.CreateTimer();
        _busyTimer.Interval = TimeSpan.FromSeconds(30);
        _busyTimer.Tick += (_, _) => TickGuard.Run("busy", CheckBusy);
        _busyTimer.Start();
        CheckBusy();

        UpdateTray();
        Logger.Log("started");
        StartDebugTriggers();
    }

    private void StartDebugTriggers()
    {
        if (AppData.DebugSettings) OpenSettings();
        if (AppData.DebugMain) OpenMain();
        else if (!_ui.FirstRunShown)
        {
            _ui.FirstRunShown = true;
            SaveState();
            OpenMain();
        }
        if (AppData.DebugEyeCue)
        {
            _debugEyeTimer = _dq.CreateTimer();
            _debugEyeTimer.Interval = TimeSpan.FromSeconds(3);
            _debugEyeTimer.Tick += (t, _) => TickGuard.Run("debug-eye", () =>
            {
                if (t.Interval != TimeSpan.FromSeconds(40))
                {
                    t.Stop();
                    t.Interval = TimeSpan.FromSeconds(40);
                    t.Start();
                }
                Logger.Log("debug eye cue");
                ShowEyeCue();
            });
            _debugEyeTimer.Start();
        }
        if (AppData.DebugCrash)
        {
            var crashTimer = _dq.CreateTimer();
            crashTimer.IsRepeating = false;
            crashTimer.Interval = TimeSpan.FromSeconds(3);
            // An exception escaping a dispatcher timer callback fails fast before Application.UnhandledException is raised,
            // so the test exception is caught here and goes through the same logging and reporting as a UI one.
            crashTimer.Tick += (_, _) => TickGuard.Run("debug-crash", () =>
            {
                try { throw new InvalidOperationException("Desk Pal crash report test"); }
                catch (Exception ex) { LogCrash("ui unhandled (debug test)", ex, null, "Application.UnhandledException", handled: true); }
            });
            crashTimer.Start();
            _debugCrashTimer = crashTimer;
        }
        if (AppData.DebugTray && _icons is not null)
        {
            var loops = _icons.LoopNames.ToArray();
            var next = 0;
            _debugTrayTimer = _dq.CreateTimer();
            _debugTrayTimer.Interval = TimeSpan.FromSeconds(4);
            _debugTrayTimer.Tick += (_, _) => TickGuard.Run("debug-tray", () =>
            {
                var loop = loops[next++ % loops.Length];
                Logger.Log("debug tray loop " + loop);
                _anim?.Play(loop, 4000, force: true);
            });
            _debugTrayTimer.Start();
        }
        if (AppData.DebugQuickPanel || AppData.DebugTrayMenu)
        {
            _debugPanelTimer = _dq.CreateTimer();
            _debugPanelTimer.IsRepeating = false;
            _debugPanelTimer.Interval = TimeSpan.FromSeconds(2);
            _debugPanelTimer.Tick += (_, _) => TickGuard.Run("debug-panel", () =>
            {
                if (AppData.DebugQuickPanel) ShowQuick();
                else ShowMenu();
            });
            _debugPanelTimer.Start();
        }
        if (AppData.DebugNudge)
        {
            _debugNudgeTimer = _dq.CreateTimer();
            _debugNudgeTimer.IsRepeating = false;
            _debugNudgeTimer.Interval = TimeSpan.FromSeconds(3);
            _debugNudgeTimer.Tick += (_, _) => TickGuard.Run("debug-nudge", () =>
            {
                var ok = _router.Execute(new TestNudgeCommand());
                Logger.Log("debug nudge " + (ok ? "requested" : "refused"));
            });
            _debugNudgeTimer.Start();
        }
    }

    private void SetStyle(string style)
    {
        _settings.AvatarStyle = style;
        _hub.Commit(false);
    }

    // On sends reports (and the ones waiting from Ask); off is Never, which keeps and sends nothing.
    private void ToggleCrashReports()
    {
        _settings.CrashReports = _settings.CrashReports != CrashReportingMode.Always ? CrashReportingMode.Always : CrashReportingMode.Never;
        _hub.Commit(false);
    }

    private void ToggleAnimateTray()
    {
        _settings.AnimateTray = !_settings.AnimateTray;
        _hub.Commit(false);
    }

    // Runs after every settings change, whether it came from the tray menu or the settings page.
    private void OnSettingsChanged()
    {
        if (!_settings.AnimateTray) _anim?.Stop();
        if (_settings.CrashReports != _appliedCrashMode)
        {
            _appliedCrashMode = _settings.CrashReports;
            CrashReporting.Apply(_settings.CrashReports);
            if (_settings.CrashReports == CrashReportingMode.Always) CrashReporting.SendPending();
        }
        UpdateTray();
    }

    private void OpenSettings()
    {
        _dq.TryEnqueue(() =>
        {
            try
            {
                _settingsWindow ??= new SettingsWindow(_hub);
                _settingsWindow.ShowSettings();
            }
            catch (Exception ex)
            {
                Logger.Log("settings window failed: " + ex.Message);
            }
        });
    }

    private bool IsPausedUntilTomorrow() =>
        _scheduler.ManualHold is { } h && h.Until.Date > TimeProvider.System.GetLocalNow().Date;

    private void CheckBusy()
    {
        bool busy;
        try { busy = _busy.IsBusy(); }
        catch { busy = false; }
        _calls.Poll();
        if (busy)
            _scheduler.StartHold("dnd", "Focus Assist or full screen", TimeProvider.System.GetLocalNow().AddSeconds(75));
        else
            _scheduler.EndHold("dnd");
    }

    private void UpdateTray()
    {
        if (_tray is null) return;
        var now = TimeProvider.System.GetLocalNow();
        string text;
        var manual = _scheduler.ManualHold;
        if (manual is not null)
        {
            var when = manual.Until.Date == now.Date ? manual.Until.ToString("HH:mm") : "tomorrow " + manual.Until.ToString("HH:mm");
            text = $"Paused until {when}";
        }
        else if (_scheduler.Holds.FirstOrDefault() is { } hold)
            text = $"Desk Pal: held ({hold.Reason})";
        else if (_scheduler.NextDue is { } next)
            text = $"Desk Pal: next {next.Kind.ToString().ToLowerInvariant()} {next.At:HH:mm}";
        else
            text = "Desk Pal";
        _tray.ToolTipText = text;
        if (_quick?.IsOpen == true) _quick.Refresh();
        if (_menu?.IsOpen == true) _menu.Refresh();
        _anim?.Refresh();
    }

    private void OnSchedulerEvent(SchedulerEvent e)
    {
        Logger.Log($"{e.Type} {e.Nudge?.Title ?? e.Hold?.Source}");
        switch (e.Type)
        {
            case SchedulerEventType.EyeCue:
                ShowEyeCue();
                SaveState();
                return;
            case SchedulerEventType.NudgeShown:
                _pill?.Dismiss();
                _anim?.Stop();
                if (e.Nudge is not null)
                {
                    _anim?.Play(LoopFor(e.Nudge), 10_000);
                    PresentAsync(e.Nudge);
                }
                return;
            case SchedulerEventType.NudgeDone:
                _anim?.Play("celebrate", 3000);
                _card?.CelebrateThenHide();
                ClearToast();
                break;
            case SchedulerEventType.NudgeSkipped:
            case SchedulerEventType.NudgeSnoozed:
                _card?.HideCard();
                ClearToast();
                break;
        }
        SaveState();
        UpdateTray();
    }

    private string LoopFor(Nudge nudge)
    {
        if (nudge.Message == NudgeScheduler.LongCallMessage) return "jumps";
        if (nudge.Kinds.Contains(NudgeKind.Walk)) return ++_walkNudges % 2 == 0 ? "fresh-air" : "walk";
        if (nudge.Kinds.Contains(NudgeKind.Water)) return "drink";
        if (nudge.Kinds.Contains(NudgeKind.Stretch)) return "stretch";
        if (nudge.Kinds.Contains(NudgeKind.Eyes)) return "eyes";
        return "fresh-air";
    }

    private void SaveState()
    {
        try
        {
            var snapshot = _scheduler.Snapshot();
            snapshot.Ui = _ui;
            JsonFileStore.Save(AppData.StatePath, snapshot);
        }
        catch (Exception ex) { Logger.Log("state save failed: " + ex.Message); }
    }

    private async void PresentAsync(Nudge nudge)
    {
        if (_settings.UseNudgeCard)
        {
            try
            {
                await ShowCardAsync(nudge);
                return;
            }
            catch (Exception ex)
            {
                Logger.Log("card failed, using toast: " + ex.Message);
            }
        }
        await ShowToastAsync(nudge);
    }

    private Task ShowCardAsync(Nudge nudge)
    {
        if (_card is null)
        {
            _card = new NudgeCardWindow(_router);
            _card.Expired += id =>
            {
                Logger.Log("card auto-hidden");
                _scheduler.Expire(id);
            };
        }
        return _card.ShowNudgeAsync(nudge, AppData.Debug3D ? "3d" : _settings.AvatarStyle);
    }

    private async Task ShowToastAsync(Nudge nudge)
    {
        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(ToastTag, ToastTag);
            var id = nudge.Id.ToString();
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddArgument("id", id)
                .AddText(nudge.Title)
                .AddText(nudge.Message);
            if (nudge.Instruction is not null) builder.AddText(nudge.Instruction);
            var toast = builder
                .AddButton(new AppNotificationButton("Start").SetButtonStyle(AppNotificationButtonStyle.Success).AddArgument("action", "start").AddArgument("id", id))
                .AddButton(new AppNotificationButton("In 10 min").AddArgument("action", "snooze").AddArgument("id", id))
                .AddButton(new AppNotificationButton("Skip").AddArgument("action", "skip").AddArgument("id", id))
                .BuildNotification();
            toast.Tag = ToastTag;
            toast.Group = ToastTag;
            toast.Expiration = DateTimeOffset.Now.AddMinutes(10);
            AppNotificationManager.Default.Show(toast);
            ArmToastTimer(nudge.Id);
        }
        catch (Exception ex)
        {
            Logger.Log("toast failed: " + ex.Message);
        }
    }

    // An ignored toast must not leave the scheduler waiting on it forever.
    private void ArmToastTimer(Guid id)
    {
        _toastNudgeId = id;
        _toastTimer ??= _dq.CreateTimer();
        _toastTimer.Stop();
        _toastTimer.IsRepeating = false;
        _toastTimer.Interval = TimeSpan.FromMinutes(10);
        _toastTimer.Tick -= OnToastExpired;
        _toastTimer.Tick += OnToastExpired;
        _toastTimer.Start();
    }

    private void OnToastExpired(DispatcherQueueTimer sender, object args) =>
        TickGuard.Run("toast-expiry", ToastExpired);

    private void ToastExpired()
    {
        if (_toastNudgeId is { } id && _card?.CurrentId != id)
        {
            Logger.Log("toast expired");
            _scheduler.Expire(id);
        }
        ClearToast();
    }

    private void ClearToast()
    {
        _toastTimer?.Stop();
        if (_toastNudgeId is null) return;
        _toastNudgeId = null;
        _ = RemoveToastAsync();
    }

    private static async Task RemoveToastAsync()
    {
        try { await AppNotificationManager.Default.RemoveByTagAndGroupAsync(ToastTag, ToastTag); }
        catch (Exception ex) { Logger.Log("toast remove failed: " + ex.Message); }
    }

    private async void OpenCardFor(Guid? id)
    {
        if (_scheduler.Current is not { } nudge || (id is not null && id != nudge.Id)) return;
        try
        {
            await ShowCardAsync(nudge);
            ClearToast();
        }
        catch (Exception ex)
        {
            Logger.Log("card failed from toast: " + ex.Message);
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var action = args.Arguments.TryGetValue("action", out var a) ? a : "";
        Guid? id = args.Arguments.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g) ? g : null;
        Logger.Log($"toast action: {action}");
        switch (action)
        {
            case "open":
            case "start":
                _dq.TryEnqueue(() => OpenCardFor(id));
                break;
            case "snooze":
                _dq.TryEnqueue(() => _router.Execute(new SnoozeCommand(id)));
                break;
            case "skip":
                _dq.TryEnqueue(() => _router.Execute(new SkipCommand(id)));
                break;
        }
    }

    private void ShowEyeCue()
    {
        try
        {
            if (_pill is null)
            {
                _pill = new EyeCuePillWindow();
                _pill.Finished += clicked =>
                {
                    Logger.Log(clicked ? "eye cue clicked" : "eye cue faded");
                    _anim?.Stop();
                };
            }
            _pill.ShowCue();
            _anim?.Play("eyes", 20_000);
        }
        catch (Exception ex)
        {
            Logger.Log("eye cue failed: " + ex.Message);
        }
    }

    private TrayContext PanelContext() =>
        new(_scheduler, _hub, ShowEyeCue, OpenMain, SetStyle, ToggleAnimateTray, ToggleCrashReports, ExitApp);

    // A left click waits out the double-click time before opening the quick panel, so the first click of a
    // double-click never flashes it; the double-click cancels the wait.
    private void OnTrayLeftClick()
    {
        if (Environment.TickCount64 - _lastDoubleClickTick < GetDoubleClickTime() + 250) return;
        if (_quick is { IsOpen: true } or { JustDeactivated: true })
        {
            _clickTimer?.Stop();
            _quick.HidePanel();
            return;
        }
        if (_clickTimer is null)
        {
            _clickTimer = _dq.CreateTimer();
            _clickTimer.IsRepeating = false;
            _clickTimer.Tick += (_, _) => TickGuard.Run("tray-click-delay", ShowQuick);
        }
        _clickTimer.Stop();
        _clickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _clickTimer.Start();
    }

    private void OnTrayDoubleClick()
    {
        _clickTimer?.Stop();
        _lastDoubleClickTick = Environment.TickCount64;
        _quick?.HidePanel();
        _menu?.HidePanel();
        OpenMain();
    }

    private void OnTrayRightClick()
    {
        _clickTimer?.Stop();
        if (_menu is { IsOpen: true } or { JustDeactivated: true })
        {
            _menu.HidePanel();
            return;
        }
        ShowMenu();
    }

    private void ShowQuick()
    {
        try
        {
            _quick ??= new QuickPanelWindow(PanelContext());
            _menu?.HidePanel();
            var anchor = TrayAnchor.Get(_tray?.Id ?? Guid.Empty);
            if (AppData.IsDebugRun) Logger.Log($"quick panel anchor {anchor}");
            _quick.ShowPanel(anchor);
        }
        catch (Exception ex) { Logger.Log("quick panel failed: " + ex.Message); }
    }

    private void ShowMenu()
    {
        try
        {
            _menu ??= new TrayMenuWindow(PanelContext());
            _quick?.HidePanel();
            var anchor = TrayAnchor.Get(_tray?.Id ?? Guid.Empty);
            if (AppData.IsDebugRun) Logger.Log($"tray menu anchor {anchor}");
            _menu.ShowPanel(anchor);
        }
        catch (Exception ex) { Logger.Log("tray menu failed: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void OpenMain(string? page = null)
    {
        _dq.TryEnqueue(() =>
        {
            try
            {
                Logger.Log("open main window");
                _main ??= new MainWindow(
                    new MainContext(_scheduler, _hub, tag => _main?.Navigate(tag), ShowEyeCue),
                    _ui, SaveState, ShowStillHereToast);
                _main.ShowMain(page);
            }
            catch (Exception ex)
            {
                Logger.Log("main window failed: " + ex.Message);
            }
        });
    }

    private static void ShowStillHereToast()
    {
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("Desk Pal is still here in the tray.").BuildNotification());
        }
        catch (Exception ex)
        {
            Logger.Log("close hint failed: " + ex.Message);
        }
    }

    private sealed class ActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
    private void ExitApp()
    {
        _tickTimer?.Stop();
        _busyTimer?.Stop();
        _debugEyeTimer?.Stop();
        _debugNudgeTimer?.Stop();
        _debugCrashTimer?.Stop();
        _debugTrayTimer?.Stop();
        _clickTimer?.Stop();
        _debugPanelTimer?.Stop();
        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.Unregister();
        _tray?.Dispose();
        _icons?.Dispose();
        _quick?.Close();
        _menu?.Close();
        _card?.Close();
        _pill?.Close();
        _main?.Quit();
        _settingsWindow?.Quit();
        _toastTimer?.Stop();
        Logger.Log("exit");
        Exit();
    }
}


