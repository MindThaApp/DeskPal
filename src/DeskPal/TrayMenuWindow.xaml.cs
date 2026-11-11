using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeskPal;

// Right-click panel: an info strip on top, then menu-style rows. It is a window rather than a MenuFlyout so it can host the strip.
public sealed partial class TrayMenuWindow : Window
{
    private const string Check = "";
    private const string ChevronDown = "";
    private const string ChevronUp = "";

    private readonly TrayContext _ctx;
    private readonly TrayPanelHost _host;
    private readonly Button _resume;
    private readonly Button _pauseToggle;
    private readonly FontIcon _pauseChevron;
    private readonly StackPanel _pauseSub = new() { Spacing = 2, Visibility = Visibility.Collapsed };
    private readonly Button _styleToggle;
    private readonly FontIcon _styleChevron;
    private readonly StackPanel _styleSub = new() { Spacing = 2, Visibility = Visibility.Collapsed };
    private readonly (string Key, Button Button, FontIcon Trail)[] _styles;
    private readonly (Button Button, FontIcon Trail) _animate;
    private readonly (Button Button, FontIcon Trail) _crash;

    public TrayMenuWindow(TrayContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        AddRow("", "Open Desk Pal", () => _ctx.OpenMain(null), bold: true);
        AddRow("", "Stretch now", () => _ctx.Scheduler.Execute(new TestNudgeCommand()));

        (_pauseToggle, _pauseChevron) = AddExpander("", "Pause", _pauseSub);
        AddRow("", "30 min", () => _ctx.Scheduler.Execute(new PauseCommand(Minutes: 30)), into: _pauseSub, indent: 36);
        AddRow("", "1 hour", () => _ctx.Scheduler.Execute(new PauseCommand(Minutes: 60)), into: _pauseSub, indent: 36);
        AddRow("", "Until tomorrow", () => _ctx.Scheduler.Execute(new PauseCommand(UntilTomorrow: true)), into: _pauseSub, indent: 36);
        _resume = AddRow("", "Resume", () => _ctx.Scheduler.Execute(new ResumeCommand())).Button;

        (_styleToggle, _styleChevron) = AddExpander("", "Style", _styleSub);
        _styles =
        [
            StyleRow("flat", "Flat"),
            StyleRow("soft3d", "Soft 3D"),
            StyleRow("3d", "3D (beta)"),
        ];

        _animate = AddRow("", "Animate tray icon", () => _ctx.ToggleAnimateTray(), keepOpen: true);
        _crash = AddRow("", "Send crash reports", () => _ctx.ToggleCrashReports(), keepOpen: true);
        AddRow("", "Settings", () => _ctx.OpenMain("settings"));
        AddRow("", "Exit", () => _ctx.Exit(), into: ExitRows);

        _host = new TrayPanelHost(this, Root, 340);
        _ctx.Hub.Changed += () => TickGuard.Run("tray-menu-settings", () => { if (_host.IsOpen) Refresh(); });
    }

    public bool IsOpen => _host.IsOpen;

    public bool JustDeactivated => _host.JustDeactivated;

    public void ShowPanel(TrayAnchor anchor)
    {
        _pauseSub.Visibility = Visibility.Collapsed;
        _styleSub.Visibility = Visibility.Collapsed;
        Refresh();
        _host.Show(anchor);
        (Rows.Children.FirstOrDefault() as Control)?.Focus(FocusState.Programmatic);
    }

    public void HidePanel() => _host.Hide();

    public void Refresh()
    {
        var s = _ctx.Scheduler;
        var settings = _ctx.Hub.Settings;
        GreetingText.Text = HomeInfo.Greeting(DateTime.Now.Hour);
        NextText.Text = "Next up: " + HomeInfo.NextUp(s);
        var manual = s.ManualHold is not null;
        var status = HomeInfo.Status(s, TimeProvider.System.GetLocalNow());
        var holds = s.Holds;
        HoldsText.Text = manual || holds.Count > 0
            ? (manual ? status : "") + (manual && holds.Count > 0 ? " · " : "") + (holds.Count > 0 ? "Holding: " + string.Join(", ", holds.Select(h => h.Reason)) : "")
            : "";
        HoldsText.Visibility = HoldsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsText.Text = $"Today: {s.StretchesToday} stretches · {s.EyeBreaksToday} eye breaks · {s.WalksToday} walks";
        _resume.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;

        var current = settings.AvatarStyle is "soft3d" or "3d" ? settings.AvatarStyle : "flat";
        foreach (var (key, button, trail) in _styles) SetChecked(button, trail, key == current);
        SetChecked(_animate.Button, _animate.Trail, settings.AnimateTray);
        SetChecked(_crash.Button, _crash.Trail, settings.CrashReports == CrashReportingMode.Always);
        _host.Relayout();
    }

    private static void SetChecked(Button button, FontIcon trail, bool on)
    {
        trail.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var name = AutomationProperties.GetHelpText(button);
        AutomationProperties.SetName(button, name + (on ? ", on" : ", off"));
    }

    private (string, Button, FontIcon) StyleRow(string key, string text)
    {
        var (button, trail) = AddRow("", text, () => _ctx.SetStyle(key), into: _styleSub, indent: 36, keepOpen: true);
        return (key, button, trail);
    }

    private (Button, FontIcon) AddExpander(string glyph, string text, StackPanel sub)
    {
        var (button, trail) = AddRow(glyph, text, () => { }, keepOpen: true);
        trail.Glyph = ChevronDown;
        trail.Visibility = Visibility.Visible;
        button.Click += (_, _) => TickGuard.Run("tray-menu-expand", () =>
        {
            var open = sub.Visibility == Visibility.Collapsed;
            sub.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            trail.Glyph = open ? ChevronUp : ChevronDown;
            _host.Relayout();
        });
        Rows.Children.Add(sub);
        return (button, trail);
    }

    private (Button Button, FontIcon Trail) AddRow(string glyph, string text, Action click, bool bold = false,
        Panel? into = null, double indent = 0, bool keepOpen = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        var label = new TextBlock
        {
            Text = text,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var trail = new FontIcon { Glyph = Check, FontSize = 12, Visibility = Visibility.Collapsed };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(trail, 2);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(trail);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12 + indent, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            MinHeight = 36,
        };
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, text);
        button.Click += (_, _) => TickGuard.Run("tray-menu-row", () =>
        {
            if (!keepOpen) _host?.Hide();
            click();
        });
        (into ?? Rows).Children.Add(button);
        return (button, trail);
    }
}
