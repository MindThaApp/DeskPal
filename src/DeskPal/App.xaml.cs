using System.Drawing;
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
    private static Mutex? _instanceMutex;

    private DispatcherQueue _dq = null!;
    private DeskPalSettings _settings = new();
    private NudgeScheduler _scheduler = null!;
    private ICommandRouter _router = null!;
    private readonly IBusyStateProvider _busy = new ShellBusyStateProvider();
    private TaskbarIcon? _tray;
    private NudgeCardWindow? _card;
    private DispatcherQueueTimer? _tickTimer;
    private DispatcherQueueTimer? _busyTimer;
    private MenuFlyoutItem? _resumeItem;
    private ToggleMenuFlyoutItem? _flatItem;
    private ToggleMenuFlyoutItem? _softItem;

    public App()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _instanceMutex = new Mutex(true, "DeskPal-" + sid, out var created);
        if (!created) Environment.Exit(0);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        _settings = JsonFileStore.Load<DeskPalSettings>(AppData.SettingsPath, TimeProvider.System);
        var state = JsonFileStore.Load<DeskPalState>(AppData.StatePath, TimeProvider.System);
        _scheduler = new NudgeScheduler(TimeProvider.System, _settings, state);
        _router = _scheduler;
        _scheduler.Event += e => _dq.TryEnqueue(() => OnSchedulerEvent(e));

        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnNotificationInvoked;
        manager.Register();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "deskpal.ico");
        _tray = new TaskbarIcon
        {
            ToolTipText = "Desk Pal",
            ContextFlyout = BuildMenu(),
            Icon = new Icon(iconPath),
        };
        _tray.ForceCreate();

        _tickTimer = _dq.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(5);
        _tickTimer.Tick += (_, _) => { _scheduler.Tick(); UpdateTray(); };
        _tickTimer.Start();

        _busyTimer = _dq.CreateTimer();
        _busyTimer.Interval = TimeSpan.FromSeconds(30);
        _busyTimer.Tick += (_, _) => CheckBusy();
        _busyTimer.Start();
        CheckBusy();

        UpdateTray();
        Logger.Log("started");
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();

        var test = new MenuFlyoutItem { Text = "Test nudge" };
        test.Click += (_, _) => _router.Execute(new TestNudgeCommand());

        var pause = new MenuFlyoutSubItem { Text = "Pause" };
        pause.Items.Add(PauseItem("30 min", new PauseCommand(Minutes: 30)));
        pause.Items.Add(PauseItem("1 hour", new PauseCommand(Minutes: 60)));
        pause.Items.Add(PauseItem("Until tomorrow", new PauseCommand(UntilTomorrow: true)));

        _resumeItem = new MenuFlyoutItem { Text = "Resume", IsEnabled = false };
        _resumeItem.Click += (_, _) => _router.Execute(new ResumeCommand());

        var style = new MenuFlyoutSubItem { Text = "Style" };
        _flatItem = new ToggleMenuFlyoutItem { Text = "Flat" };
        _softItem = new ToggleMenuFlyoutItem { Text = "Soft 3D" };
        _flatItem.Click += (_, _) => SetStyle("flat");
        _softItem.Click += (_, _) => SetStyle("soft3d");
        style.Items.Add(_flatItem);
        style.Items.Add(_softItem);
        RefreshStyleChecks();

        var exit = new MenuFlyoutItem { Text = "Exit" };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(test);
        menu.Items.Add(pause);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(style);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        menu.Opening += (_, _) => UpdateTray();
        return menu;
    }

    private MenuFlyoutItem PauseItem(string text, Command command)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => _router.Execute(command);
        return item;
    }

    private void SetStyle(string style)
    {
        _settings.AvatarStyle = style;
        RefreshStyleChecks();
        try { JsonFileStore.Save(AppData.SettingsPath, _settings); }
        catch (Exception ex) { Logger.Log("settings save failed: " + ex.Message); }
    }

    private void RefreshStyleChecks()
    {
        if (_flatItem is null || _softItem is null) return;
        _flatItem.IsChecked = _settings.AvatarStyle != "soft3d";
        _softItem.IsChecked = _settings.AvatarStyle == "soft3d";
    }

    private void CheckBusy()
    {
        bool busy;
        try { busy = _busy.IsBusy(); }
        catch { busy = false; }
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
        if (_resumeItem is not null) _resumeItem.IsEnabled = manual is not null;
    }

    private void OnSchedulerEvent(SchedulerEvent e)
    {
        Logger.Log($"{e.Type} {e.Nudge?.Title ?? e.Hold?.Source}");
        switch (e.Type)
        {
            case SchedulerEventType.NudgeShown:
                if (e.Nudge is not null) PresentAsync(e.Nudge);
                return;
            case SchedulerEventType.NudgeDone:
            case SchedulerEventType.NudgeSkipped:
            case SchedulerEventType.NudgeSnoozed:
                _card?.HideCard();
                break;
        }
        SaveState();
        UpdateTray();
    }

    private void SaveState()
    {
        try { JsonFileStore.Save(AppData.StatePath, _scheduler.Snapshot()); }
        catch (Exception ex) { Logger.Log("state save failed: " + ex.Message); }
    }

    private async void PresentAsync(Nudge nudge)
    {
        if (_settings.UseNudgeCard)
        {
            try
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
                await _card.ShowNudgeAsync(nudge, _settings.AvatarStyle);
                return;
            }
            catch (Exception ex)
            {
                Logger.Log("card failed, using toast: " + ex.Message);
            }
        }
        ShowToast(nudge);
    }

    private static void ShowToast(Nudge nudge)
    {
        var id = nudge.Id.ToString();
        var builder = new AppNotificationBuilder().AddText(nudge.Title).AddText(nudge.Message);
        if (nudge.Instruction is not null) builder.AddText(nudge.Instruction);
        var toast = builder
            .AddButton(new AppNotificationButton("Done").AddArgument("action", "done").AddArgument("id", id))
            .AddButton(new AppNotificationButton("Snooze 5 min").AddArgument("action", "snooze").AddArgument("id", id))
            .AddButton(new AppNotificationButton("Skip").AddArgument("action", "skip").AddArgument("id", id))
            .BuildNotification();
        AppNotificationManager.Default.Show(toast);
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var action = args.Arguments.TryGetValue("action", out var a) ? a : "";
        Guid? id = args.Arguments.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g) ? g : null;
        Logger.Log($"toast action: {action}");
        Command? command = action switch
        {
            "done" => new DoneCommand(id),
            "snooze" => new SnoozeCommand(id),
            "skip" => new SkipCommand(id),
            _ => null,
        };
        if (command is not null) _dq.TryEnqueue(() => _router.Execute(command));
    }

    private void ExitApp()
    {
        _tickTimer?.Stop();
        _busyTimer?.Stop();
        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.Unregister();
        _tray?.Dispose();
        _card?.Close();
        Logger.Log("exit");
        Exit();
    }
}
