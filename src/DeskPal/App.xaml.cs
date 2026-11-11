using System.Drawing;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DeskPal;

public partial class App : Application
{
    private static readonly TimeSpan SnoozeDelay = TimeSpan.FromMinutes(5);
    private TaskbarIcon? _tray;
    private Timer? _startupTimer;
    private Timer? _snoozeTimer;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnNotificationInvoked;
        manager.Register();

        var menu = new MenuFlyout();
        var test = new MenuFlyoutItem { Text = "Test nudge" };
        test.Click += (_, _) => ShowNudge();
        var exit = new MenuFlyoutItem { Text = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(test);
        menu.Items.Add(exit);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "deskpal.ico");
        _tray = new TaskbarIcon
        {
            ToolTipText = "Desk Pal",
            ContextFlyout = menu,
            Icon = new Icon(iconPath),
        };
        _tray.ForceCreate();

        Log("started");
        _startupTimer = new Timer(_ => ShowNudge(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private static void ShowNudge()
    {
        var toast = new AppNotificationBuilder()
            .AddText("Stretch time")
            .AddText("Reach for the sky with Bloop, 20 seconds.")
            .AddButton(new AppNotificationButton("Done").AddArgument("action", "done"))
            .AddButton(new AppNotificationButton("Snooze 5 min").AddArgument("action", "snooze"))
            .AddButton(new AppNotificationButton("Skip").AddArgument("action", "skip"))
            .BuildNotification();
        AppNotificationManager.Default.Show(toast);
        Log("nudge shown");
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var action = args.Arguments.TryGetValue("action", out var a) ? a : "(body click)";
        Log($"toast action: {action}");
        if (action == "snooze")
        {
            _snoozeTimer?.Dispose();
            _snoozeTimer = new Timer(_ => ShowNudge(), null, SnoozeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void ExitApp()
    {
        _startupTimer?.Dispose();
        _snoozeTimer?.Dispose();
        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.Unregister();
        _tray?.Dispose();
        Log("exit");
        Exit();
    }

    private static readonly object LogLock = new();

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPal", "logs");
            Directory.CreateDirectory(dir);
            lock (LogLock)
                File.AppendAllText(Path.Combine(dir, "deskpal.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
