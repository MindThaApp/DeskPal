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

    public QuickPanelWindow(TrayContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _stats = [BuildStat(StretchStat, "Stretches"), BuildStat(EyeStat, "Eye breaks"), BuildStat(WalkStat, "Walks")];
        _host = new TrayPanelHost(this, Root, 320);
        _host.Placed += p => TickGuard.Run("quick-bloop-place", () => BloopStage.Move(this, p));
        _host.Hidden += () => TickGuard.Run("quick-bloop-leave", () => BloopStage.Release(this));
        Closed += (_, _) => BloopStage.CloseAll();
        SoundButton.Visibility = SoundsSetting is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Bloop sits in its own clear window beside the card (shared with the nudge card), so the card holds only text
    // and buttons.
    private static BloopBuddyWindow? Bloop => BloopStage.Window;

    // The interaction sounds switch lives in the shared settings; the toggle shows only when that setting exists.
    private static readonly System.Reflection.PropertyInfo? SoundsSetting =
        typeof(DeskPalSettings).GetProperty("InteractionSounds") is { PropertyType: var t } prop && t == typeof(bool) ? prop : null;

    public bool IsOpen => _host.IsOpen;

    public bool JustDeactivated => _host.JustDeactivated;

    public void ShowPanel(TrayAnchor anchor)
    {
        Refresh();
        _host.Show(anchor);
        StretchButton.Focus(FocusState.Programmatic);
        if (_host.Placement is { } placement)
        {
            BloopStage.Claim(new BloopClaim(this, _host.Hwnd, placement, _ctx.Hub.Settings.AvatarStyle, "bloop-idle"));
            if (Bloop is { } bloop)
            {
                _host.CompanionHwnd = bloop.Hwnd;
                _host.CompanionInteracting = () => bloop.IsInteracting;
                _host.CompanionReleaseTick = () => bloop.LastReleaseTick;
            }
        }
    }

    public void HidePanel() => _host.Hide();

    // Debug only: pulls Bloop away through the drag code, and with --debug-bloop-exit closes the panel while away.
    public void DebugActivateBloop() { Bloop?.DebugMouseActivate(); _host.DebugDeactivate(); }

    public void DebugDragBloop(System.Numerics.Vector2 by) => Bloop?.DebugDrag(by);

    private void OnSound(object sender, RoutedEventArgs e) => TickGuard.Run("quick-sound", () =>
    {
        if (SoundsSetting is null) return;
        var on = (bool)SoundsSetting.GetValue(_ctx.Hub.Settings)!;
        SoundsSetting.SetValue(_ctx.Hub.Settings, !on);
        _ctx.Hub.Commit(false);
        UpdateSound();
    });

    private void UpdateSound()
    {
        if (SoundsSetting is null) return;
        var on = (bool)SoundsSetting.GetValue(_ctx.Hub.Settings)!;
        SoundIcon.Glyph = on ? "" : "";
        var name = on ? "Interaction sounds on. Turn off" : "Interaction sounds off. Turn on";
        AutomationProperties.SetName(SoundButton, name);
        ToolTipService.SetToolTip(SoundButton, on ? "Sounds on" : "Sounds off");
    }

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
        UpdateSound();
        _host.Relayout();
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
