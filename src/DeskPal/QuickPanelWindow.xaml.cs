using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace DeskPal;

// What the two tray panels need from the app.
public sealed record TrayContext(
    NudgeScheduler Scheduler,
    SettingsHub Hub,
    Action ShowEyeCue,
    Action<string?> OpenMain,
    Action<string> SetStyle,
    Action ToggleAnimateTray,
    Action ToggleCrashReports,
    Action Exit);

// Left-click panel: Bloop, what is next, today's progress and the three quick actions.
public sealed partial class QuickPanelWindow : Window
{
    private readonly TrayContext _ctx;
    private readonly TrayPanelHost _host;
    private readonly (ProgressRing Ring, TextBlock Count, TextBlock Name)[] _stats;
    private string _style = "";
    private long _loadVersion;

    public QuickPanelWindow(TrayContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _stats = [BuildStat(StretchStat, "Stretches"), BuildStat(EyeStat, "Eye breaks"), BuildStat(WalkStat, "Walks")];
        _host = new TrayPanelHost(this, Root, 320);
    }

    public bool IsOpen => _host.IsOpen;

    public bool JustDeactivated => _host.JustDeactivated;

    public void ShowPanel(TrayAnchor anchor)
    {
        Refresh();
        _host.Show(anchor);
        StretchButton.Focus(FocusState.Programmatic);
    }

    public void HidePanel() => _host.Hide();

    public void Refresh()
    {
        var s = _ctx.Scheduler;
        StatusText.Text = HomeInfo.Status(s, TimeProvider.System.GetLocalNow());
        SubText.Text = s.Holds.Count > 0 && s.ManualHold is not null ? HomeInfo.HoldsLine(s) : HomeInfo.Greeting(DateTime.Now.Hour);
        var goals = DailyGoals.For(_ctx.Hub.Settings);
        SetStat(_stats[0], s.StretchesToday, goals.Stretches);
        SetStat(_stats[1], s.EyeBreaksToday, goals.EyeBreaks);
        SetStat(_stats[2], s.WalksToday, goals.Walks);
        var paused = s.ManualHold is not null;
        PauseButton.Content = paused ? "Resume" : "Pause 30 min";
        AutomationProperties.SetName(PauseButton, paused ? "Resume reminders" : "Pause 30 minutes");
        _ = LoadAvatarAsync();
        _host.Relayout();
    }

    private async Task LoadAvatarAsync()
    {
        var style = AppData.Debug3D ? "3d" : _ctx.Hub.Settings.AvatarStyle;
        if (style == _style && Avatar.Source is not null) return;
        _style = style;
        var version = ++_loadVersion;
        var source = await NudgeCardWindow.LoadAvatarAsync(style, "bloop-idle", 256);
        if (version == _loadVersion) Avatar.Source = source;
    }

    private static (ProgressRing, TextBlock, TextBlock) BuildStat(StackPanel host, string name)
    {
        var ring = new ProgressRing { Width = 46, Height = 46, IsIndeterminate = false, Minimum = 0, Maximum = 1, Value = 0, IsActive = true,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x80, 0x80, 0x80)) };
        var count = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = name, FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center };
        var face = new Grid { Width = 46, Height = 46 };
        face.Children.Add(ring);
        face.Children.Add(count);
        host.Children.Add(face);
        host.Children.Add(label);
        AutomationProperties.SetAccessibilityView(ring, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return (ring, count, label);
    }

    private static void SetStat((ProgressRing Ring, TextBlock Count, TextBlock Name) stat, int done, int goal)
    {
        stat.Ring.Maximum = Math.Max(1, goal);
        stat.Ring.Value = Math.Min(done, Math.Max(1, goal));
        stat.Count.Text = goal > 0 ? $"{done}/{goal}" : done.ToString();
        var text = goal > 0 ? $"{stat.Name.Text}: {done} of {goal} today" : $"{stat.Name.Text}: {done} today, no goal";
        AutomationProperties.SetName(stat.Count.Parent as FrameworkElement ?? stat.Count, text);
    }

    private void OnAvatarPressed(object sender, PointerRoutedEventArgs e)
    {
        var d = new Duration(TimeSpan.FromMilliseconds(650));
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 4 };
        var sy = new DoubleAnimation { From = 0.78, To = 1, Duration = d, EasingFunction = ease };
        var sx = new DoubleAnimation { From = 1.14, To = 1, Duration = d, EasingFunction = ease };
        Storyboard.SetTarget(sy, Squish);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        Storyboard.SetTarget(sx, Squish);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        var sb = new Storyboard();
        sb.Children.Add(sy);
        sb.Children.Add(sx);
        sb.Begin();
    }

    private void OnStretch(object sender, RoutedEventArgs e) => TickGuard.Run("quick-stretch", () =>
    {
        _host.Hide();
        _ctx.Scheduler.Execute(new TestNudgeCommand());
    });

    private void OnEye(object sender, RoutedEventArgs e) => TickGuard.Run("quick-eye", () =>
    {
        _host.Hide();
        _ctx.ShowEyeCue();
    });

    private void OnPause(object sender, RoutedEventArgs e) => TickGuard.Run("quick-pause", () =>
    {
        _host.Hide();
        _ctx.Scheduler.Execute(_ctx.Scheduler.ManualHold is null ? new PauseCommand(Minutes: 30) : new ResumeCommand());
    });

    private void OnOpen(object sender, RoutedEventArgs e) => TickGuard.Run("quick-open", () =>
    {
        _host.Hide();
        _ctx.OpenMain(null);
    });
}
