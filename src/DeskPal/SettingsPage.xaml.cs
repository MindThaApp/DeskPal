using DeskPal.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskPal;

// Host it anywhere: new SettingsPage(hub). It refreshes itself when the hub reports a change from elsewhere (for example the tray menu).
public sealed partial class SettingsPage : Page
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly SettingsHub _hub;
    private readonly DeskPalSettings _s;
    private readonly List<Action> _syncs = [];
    private bool _loading;

    public SettingsPage(SettingsHub hub)
    {
        _hub = hub;
        _s = hub.Settings;
        InitializeComponent();
        Build();
        Loaded += (_, _) =>
        {
            _hub.Changed -= Sync;
            _hub.Changed += Sync;
            Sync();
        };
        Unloaded += (_, _) => _hub.Changed -= Sync;
    }

    private void Sync()
    {
        _loading = true;
        try { foreach (var s in _syncs) s(); }
        finally { _loading = false; }
    }

    private void Build()
    {
        Root.Children.Add(new TextBlock { Text = "Settings", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        Root.Children.Add(Header("Reminders"));

        AddReminder("Movement", "Stretch or stand up",
            "Breaking up sitting about every 30 minutes is widely recommended to ease stiffness.",
            () => _s.MovementEnabled, b => _s.MovementEnabled = b,
            DeskPalSettings.MovementChoices, v => $"{v} min", DeskPalSettings.RecommendedMovementMinutes,
            () => _s.MovementMinutes, v => _s.StretchMinutes = v,
            () => _s.MovementEnabled && _s.MovementMinutes == DeskPalSettings.RecommendedMovementMinutes,
            custom: true);

        AddReminder("Eyes", "20-20-20 eye break (a quiet cue)",
            "The 20-20-20 habit: every 20 minutes, look about 20 feet away for 20 seconds.",
            () => _s.EyesEnabled, b => _s.EyesEnabled = b,
            DeskPalSettings.EyesChoices, v => $"{v} min", DeskPalSettings.RecommendedEyesMinutes,
            () => _s.EyesMinutes, v => _s.EyesMinutes = v,
            () => _s.EyesEnabled && _s.EyesMinutes == DeskPalSettings.RecommendedEyesMinutes);

        AddReminder("Water", "Drink some water",
            "Small, regular sips are easier than catching up later.",
            () => _s.Water != WaterMode.Off, b => _s.Water = b ? WaterMode.WithBreaks : WaterMode.Off,
            [0, 1], v => v == 0 ? "With movement breaks" : "Every 60 min", 0,
            () => _s.Water == WaterMode.Hourly ? 1 : 0, v => _s.Water = v == 1 ? WaterMode.Hourly : WaterMode.WithBreaks,
            () => _s.Water == WaterMode.WithBreaks);

        AddReminder("Walk", "A short walk instead of a stretch",
            "A short walk every couple of hours helps you reset.",
            () => _s.WalkAfterStretches > 0, b => _s.WalkAfterStretches = b ? DeskPalSettings.RecommendedWalkAfter : 0,
            DeskPalSettings.WalkChoices, v => $"After {v} stretches", DeskPalSettings.RecommendedWalkAfter,
            () => _s.WalkAfterStretches == 0 ? DeskPalSettings.RecommendedWalkAfter : _s.WalkAfterStretches, v => _s.WalkAfterStretches = v,
            () => _s.WalkAfterStretches == DeskPalSettings.RecommendedWalkAfter);

        AddReminder("Sit / stand", "For a standing desk",
            "If you have a standing desk, switching every 30–60 minutes keeps you comfortable.",
            () => _s.SitStandEnabled, b => _s.SitStandEnabled = b,
            DeskPalSettings.SitStandChoices, v => $"Every {v} min", DeskPalSettings.RecommendedSitStandMinutes,
            () => _s.SitStandMinutes, v => _s.SitStandMinutes = v,
            () => !_s.SitStandEnabled && _s.SitStandMinutes == DeskPalSettings.RecommendedSitStandMinutes,
            recommendedLabel: "Off by default");

        var resetAll = new Button { Content = "Reset all to recommended", Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(resetAll, "Reset all reminders to recommended");
        resetAll.Click += (_, _) =>
        {
            _s.ResetRemindersToRecommended();
            _hub.Commit(true);
        };
        Root.Children.Add(resetAll);

        Root.Children.Add(Header("Working hours"));
        AddWorkingHours();

        Root.Children.Add(Header("Appearance"));
        AddChoice("Nudge style", "How a nudge appears", ["Card", "Toast"],
            () => _s.UseNudgeCard ? 0 : 1, i => { _s.UseNudgeCard = i == 0; _hub.Commit(false); });
        AddChoice("Avatar style", "How Bloop is drawn", ["Flat", "Soft 3D", "3D (beta)"],
            () => _s.AvatarStyle == "3d" ? 2 : _s.AvatarStyle == "soft3d" ? 1 : 0,
            i => { _s.AvatarStyle = i == 2 ? "3d" : i == 1 ? "soft3d" : "flat"; _hub.Commit(false); });
        AddSwitch("Animate tray icon", "Let the tray icon move while a nudge is showing",
            () => _s.AnimateTray, b => { _s.AnimateTray = b; _hub.Commit(false); });

        Root.Children.Add(Header("Privacy"));
        AddSwitch("Send crash reports", "Help fix problems by sending a report when the app crashes",
            () => _s.CrashReports == CrashReportingMode.Always,
            b => { _s.CrashReports = b ? CrashReportingMode.Always : CrashReportingMode.Never; _hub.Commit(false); });
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 14, 0, 2),
        FontWeight = FontWeights.SemiBold,
        FontSize = 16,
    };

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private Border Card(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        return new Border { Style = (Style)Resources["CardBorder"], Child = grid };
    }

    private static StackPanel TitleBlock(string title, string sub)
    {
        var p = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        p.Children.Add(Secondary(sub));
        return p;
    }

    private void AddReminder(string name, string what, string reason,
        Func<bool> isOn, Action<bool> setOn,
        int[] values, Func<int, string> label, int recommended,
        Func<int> get, Action<int> set, Func<bool> isDefault,
        bool custom = false, string? recommendedLabel = null)
    {
        var customOpen = false;
        string Label(int v) => label(v) + (v == recommended && recommendedLabel is null ? " ★ Recommended" : "");

        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        AutomationProperties.SetName(toggle, name + " reminder on or off");
        var combo = new ComboBox { MinWidth = 270 };
        AutomationProperties.SetName(combo, name + " frequency");
        foreach (var v in values) combo.Items.Add(Label(v));
        if (custom) combo.Items.Add("Custom…");
        var box = new NumberBox
        {
            Minimum = DeskPalSettings.MovementMin,
            Maximum = DeskPalSettings.MovementMax,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Header = "Minutes (15–120)",
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(box, name + " custom minutes");

        var reset = new HyperlinkButton { Content = "Reset", Padding = new Thickness(0), FontSize = 12, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(reset, "Reset " + name + " to recommended");

        var left = TitleBlock(name + " — " + what, reason);
        if (recommendedLabel is not null) left.Children.Add(Secondary("Recommended: " + recommendedLabel.ToLowerInvariant()));
        left.Children.Add(reset);
        var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(toggle);
        right.Children.Add(combo);
        right.Children.Add(box);
        Root.Children.Add(Card(left, right));

        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            setOn(toggle.IsOn);
            _hub.Commit(true);
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedIndex < 0) return;
            if (combo.SelectedIndex < values.Length)
            {
                customOpen = false;
                set(values[combo.SelectedIndex]);
            }
            else customOpen = true;
            _hub.Commit(true);
        };
        box.ValueChanged += (_, e) =>
        {
            if (_loading || double.IsNaN(e.NewValue)) return;
            set((int)Math.Clamp(e.NewValue, box.Minimum, box.Maximum));
            _hub.Commit(true);
        };
        reset.Click += (_, _) =>
        {
            customOpen = false;
            setOn(true);
            set(recommended);
            if (recommendedLabel is not null) setOn(false);
            _hub.Commit(true);
        };

        _syncs.Add(() =>
        {
            var on = isOn();
            toggle.IsOn = on;
            combo.IsEnabled = on;
            var v = get();
            var index = Array.IndexOf(values, v);
            var showCustom = custom && (index < 0 || customOpen);
            combo.SelectedIndex = showCustom ? values.Length : index;
            box.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
            box.IsEnabled = on;
            box.Value = v;
            reset.Visibility = isDefault() ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void AddChoice(string name, string sub, string[] options, Func<int> get, Action<int> set)
    {
        var radios = new RadioButtons { MaxColumns = options.Length };
        AutomationProperties.SetName(radios, name);
        foreach (var o in options) radios.Items.Add(o);
        radios.SelectionChanged += (_, _) =>
        {
            if (_loading || radios.SelectedIndex < 0) return;
            set(radios.SelectedIndex);
        };
        _syncs.Add(() => radios.SelectedIndex = get());
        Root.Children.Add(Card(TitleBlock(name, sub), radios));
    }

    private void AddSwitch(string name, string sub, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) =>
        {
            if (!_loading) set(toggle.IsOn);
        };
        _syncs.Add(() => toggle.IsOn = get());
        Root.Children.Add(Card(TitleBlock(name, sub), toggle));
    }

    private void AddWorkingHours()
    {
        var start = new TimePicker { Header = "Start", ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        var end = new TimePicker { Header = "End", ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        AutomationProperties.SetName(start, "Working day start");
        AutomationProperties.SetName(end, "Working day end");
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        times.Children.Add(start);
        times.Children.Add(end);

        var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var buttons = new List<ToggleButton>();
        foreach (var d in Week)
        {
            var b = new ToggleButton { Content = d.ToString()[..3], MinWidth = 48 };
            AutomationProperties.SetName(b, d.ToString());
            b.Click += (_, _) =>
            {
                if (_loading) return;
                _s.WorkingHours.Days = Week.Where((_, i) => buttons[i].IsChecked == true).ToArray();
                _hub.Commit(true);
            };
            buttons.Add(b);
            days.Children.Add(b);
        }

        void TimeChanged()
        {
            if (_loading) return;
            var s = TimeOnly.FromTimeSpan(start.Time);
            var e = TimeOnly.FromTimeSpan(end.Time);
            if (e > s)
            {
                _s.WorkingHours.Start = s;
                _s.WorkingHours.End = e;
                _hub.Commit(true);
            }
            else Sync();
        }
        start.TimeChanged += (_, _) => TimeChanged();
        end.TimeChanged += (_, _) => TimeChanged();

        _syncs.Add(() =>
        {
            start.Time = _s.WorkingHours.Start.ToTimeSpan();
            end.Time = _s.WorkingHours.End.ToTimeSpan();
            for (var i = 0; i < Week.Length; i++) buttons[i].IsChecked = Array.IndexOf(_s.WorkingHours.Days, Week[i]) >= 0;
        });

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(TitleBlock("Working hours", "Reminders only come on these days and times"));
        content.Children.Add(times);
        content.Children.Add(days);
        Root.Children.Add(new Border { Style = (Style)Resources["CardBorder"], Child = content });
    }
}
