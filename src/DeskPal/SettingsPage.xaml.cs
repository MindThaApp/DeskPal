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
        Root.Children.Add(new TextBlock { Text = Loc.Get("Settings_Title"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        Root.Children.Add(Header(Loc.Get("Settings_Section_Reminders")));

        AddReminder(Loc.Get("Settings_Movement_Name"), Loc.Get("Settings_Movement_What"),
            Loc.Get("Settings_Movement_Reason"),
            () => _s.MovementEnabled, b => _s.MovementEnabled = b,
            DeskPalSettings.MovementChoices, v => Loc.Format("Settings_Minutes", v), DeskPalSettings.RecommendedMovementMinutes,
            () => _s.MovementMinutes, v => _s.StretchMinutes = v,
            () => _s.MovementEnabled && _s.MovementMinutes == DeskPalSettings.RecommendedMovementMinutes,
            custom: true);

        AddReminder(Loc.Get("Settings_Eyes_Name"), Loc.Get("Settings_Eyes_What"),
            Loc.Get("Settings_Eyes_Reason"),
            () => _s.EyesEnabled, b => _s.EyesEnabled = b,
            DeskPalSettings.EyesChoices, v => Loc.Format("Settings_Minutes", v), DeskPalSettings.RecommendedEyesMinutes,
            () => _s.EyesMinutes, v => _s.EyesMinutes = v,
            () => _s.EyesEnabled && _s.EyesMinutes == DeskPalSettings.RecommendedEyesMinutes);

        AddReminder(Loc.Get("Settings_Water_Name"), Loc.Get("Settings_Water_What"),
            Loc.Get("Settings_Water_Reason"),
            () => _s.Water != WaterMode.Off, b => _s.Water = b ? WaterMode.WithBreaks : WaterMode.Off,
            [0, 1], v => Loc.Get(v == 0 ? "Settings_WaterWithBreaks" : "Settings_WaterHourly"), 0,
            () => _s.Water == WaterMode.Hourly ? 1 : 0, v => _s.Water = v == 1 ? WaterMode.Hourly : WaterMode.WithBreaks,
            () => _s.Water == WaterMode.WithBreaks);

        AddReminder(Loc.Get("Settings_Walk_Name"), Loc.Get("Settings_Walk_What"),
            Loc.Get("Settings_Walk_Reason"),
            () => _s.WalkAfterStretches > 0, b => _s.WalkAfterStretches = b ? DeskPalSettings.RecommendedWalkAfter : 0,
            DeskPalSettings.WalkChoices, v => Loc.Format("Settings_AfterStretches", v), DeskPalSettings.RecommendedWalkAfter,
            () => _s.WalkAfterStretches == 0 ? DeskPalSettings.RecommendedWalkAfter : _s.WalkAfterStretches, v => _s.WalkAfterStretches = v,
            () => _s.WalkAfterStretches == DeskPalSettings.RecommendedWalkAfter);

        AddReminder(Loc.Get("Settings_SitStand_Name"), Loc.Get("Settings_SitStand_What"),
            Loc.Get("Settings_SitStand_Reason"),
            () => _s.SitStandEnabled, b => _s.SitStandEnabled = b,
            DeskPalSettings.SitStandChoices, v => Loc.Format("Settings_EveryMinutes", v), DeskPalSettings.RecommendedSitStandMinutes,
            () => _s.SitStandMinutes, v => _s.SitStandMinutes = v,
            () => !_s.SitStandEnabled && _s.SitStandMinutes == DeskPalSettings.RecommendedSitStandMinutes,
            recommendedLabel: Loc.Get("Settings_OffByDefault"));

        var resetAll = new Button { Content = Loc.Get("Settings_ResetAll"), Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(resetAll, Loc.Get("Settings_ResetAllAccessible"));
        resetAll.Click += (_, _) =>
        {
            _s.ResetRemindersToRecommended();
            _hub.Commit(true);
        };
        Root.Children.Add(resetAll);

        Root.Children.Add(Header(Loc.Get("Settings_Section_WorkingHours")));
        AddWorkingHours();

        Root.Children.Add(Header(Loc.Get("Settings_Section_Appearance")));
        AddChoice(Loc.Get("Settings_NudgeStyle"), Loc.Get("Settings_NudgeStyleSub"), [Loc.Get("Settings_Style_Card"), Loc.Get("Settings_Style_Toast")],
            () => _s.UseNudgeCard ? 0 : 1, i => { _s.UseNudgeCard = i == 0; _hub.Commit(false); });
        var hideChoices = DeskPalSettings.HideNudgeChoices;
        AddChoice(Loc.Get("Settings_HideAfter"), Loc.Get("Settings_HideAfterSub"),
            hideChoices.Select(v => Loc.Format("Settings_Seconds", v) + (v == DeskPalSettings.RecommendedHideNudgeSeconds ? Loc.Get("Settings_RecommendedTag") : "")).ToArray(),
            () => Math.Max(0, Array.IndexOf(hideChoices, _s.HideNudgeSeconds)),
            i => { _s.HideNudgeSeconds = hideChoices[i]; _hub.Commit(false); });
        AddChoice(Loc.Get("Settings_AvatarStyle"), Loc.Get("Settings_AvatarStyleSub"), [Loc.Get("Bloop_Style_Flat"), Loc.Get("Bloop_Style_Soft3d"), Loc.Get("Bloop_Style_3d")],
            () => _s.AvatarStyle == "3d" ? 2 : _s.AvatarStyle == "soft3d" ? 1 : 0,
            i => { _s.AvatarStyle = i == 2 ? "3d" : i == 1 ? "soft3d" : "flat"; _hub.Commit(false); });
        AddSwitch(Loc.Get("Settings_AnimateTray"), Loc.Get("Settings_AnimateTraySub"),
            () => _s.AnimateTray, b => { _s.AnimateTray = b; _hub.Commit(false); });
        AddSwitch(Loc.Get("Settings_BloopSounds"), Loc.Get("Settings_BloopSoundsSub"),
            () => _hub.InteractionSounds, b => _hub.InteractionSounds = b);

        Root.Children.Add(Header(Loc.Get("Settings_Section_Language")));
        AddLanguage();

        Root.Children.Add(Header(Loc.Get("Settings_Section_Portal")));
        AddSwitch(Loc.Get("Settings_ShareEvents"), Loc.Get("Settings_ShareEventsSub"),
            () => _s.ShareEventsWithAutomationPortal, b => { _s.ShareEventsWithAutomationPortal = b; _hub.Commit(false); });
        AddSwitch(Loc.Get("Settings_AllowControl"), Loc.Get("Settings_AllowControlSub"),
            () => _s.AllowExternalControl, b => { _s.AllowExternalControl = b; _hub.Commit(false); });
        AddAutomationPortalCard();

        Root.Children.Add(Header(Loc.Get("Settings_Section_Privacy")));
        AddSwitch(Loc.Get("Settings_CrashReports"), Loc.Get("Settings_CrashReportsSub"),
            () => _s.CrashReports == CrashReportingMode.Always,
            b => { _s.CrashReports = b ? CrashReportingMode.Always : CrashReportingMode.Never; _hub.Commit(false); });
    }

    private string CurrentLanguage() => string.IsNullOrEmpty(_s.Language) ? Loc.ResolveWindowsLanguage() : _s.Language;

    private void AddLanguage()
    {
        var picker = new LanguagePicker(CurrentLanguage());
        picker.Chosen += language => _ = ConfirmSwitchAsync(picker, language);
        _syncs.Add(() => picker.Select(CurrentLanguage()));
        Root.Children.Add(new Border { Style = (Style)Resources["CardBorder"], Padding = new Thickness(8), Child = picker });
    }

    private async Task ConfirmSwitchAsync(LanguagePicker picker, LanguageInfo language)
    {
        var switchNow = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("Language_SwitchTitle", language.NativeName),
                Content = new TextBlock { Text = Loc.Get("Language_SwitchBody"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Language_SwitchButton"),
                CloseButtonText = Loc.Get("Language_CancelButton"),
                DefaultButton = ContentDialogButton.Primary,
            };
            switchNow = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) { Logger.Log("language dialog failed: " + ex.Message); }
        if (switchNow && _hub.ChangeLanguage is { } change) change(language.Tag, true);
        else picker.Select(CurrentLanguage());
    }

    private void AddAutomationPortalCard()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(TitleBlock(Loc.Get("Settings_AppsAllowed"), Loc.Get("Settings_AppsAllowedSub")));
        var list = new StackPanel { Spacing = 4 };
        content.Children.Add(list);
        var skipped = Secondary("");
        content.Children.Add(skipped);
        var installed = AutomationPortalLink.IsInstalled();
        var link = new HyperlinkButton { Content = Loc.Get(installed ? "Settings_OpenPortal" : "Settings_GetPortal"), Padding = new Thickness(0) };
        link.Click += (_, _) => TickGuard.Run("open-automation-portal", AutomationPortalLink.OpenOrGet);
        content.Children.Add(link);
        Root.Children.Add(new Border { Style = (Style)Resources["CardBorder"], Child = content });

        _syncs.Add(() =>
        {
            list.Children.Clear();
            if (_s.ApprovedControlApps.Count == 0) list.Children.Add(Secondary(Loc.Get("Settings_None")));
            foreach (var app in _s.ApprovedControlApps.ToList())
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(new TextBlock { Text = DeskPal.Core.Integration.CallerInfo.DisplayNameFor(app) });
                var path = Secondary(app);
                path.TextWrapping = TextWrapping.NoWrap;
                path.TextTrimming = TextTrimming.CharacterEllipsis;
                name.Children.Add(path);
                var remove = new Button { Content = Loc.Get("Settings_Remove") };
                AutomationProperties.SetName(remove, Loc.Format("Settings_RemoveAccessible", DeskPal.Core.Integration.CallerInfo.DisplayNameFor(app)));
                remove.Click += (_, _) =>
                {
                    _s.ApprovedControlApps.RemoveAll(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
                    _hub.Commit(false);
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(name);
                row.Children.Add(remove);
                list.Children.Add(row);
            }
            var n = _hub.Link?.EventsSkippedToday ?? 0;
            skipped.Text = n == 1 ? Loc.Get("Settings_SkippedOne")
                : Loc.Format("Settings_SkippedMany", n);
            skipped.Visibility = _s.ShareEventsWithAutomationPortal || n > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
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

    private const double ControlColumnWidth = 280;

    private Border Card(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Same control column width on every card, so the controls line up; the label column takes the rest and wraps.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ControlColumnWidth) });
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
        string Label(int v) => label(v) + (v == recommended && recommendedLabel is null ? Loc.Get("Settings_RecommendedTag") : "");

        var toggle = new ToggleSwitch { OnContent = Loc.Get("Common_On"), OffContent = Loc.Get("Common_Off"), MinWidth = 0 };
        AutomationProperties.SetName(toggle, Loc.Format("Settings_ReminderOnOff", name));
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(combo, Loc.Format("Settings_Frequency", name));
        foreach (var v in values) combo.Items.Add(Label(v));
        if (custom) combo.Items.Add(Loc.Get("Settings_Custom"));
        var box = new NumberBox
        {
            Minimum = DeskPalSettings.MovementMin,
            Maximum = DeskPalSettings.MovementMax,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Header = Loc.Get("Settings_CustomMinutesHeader"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(box, Loc.Format("Settings_CustomMinutesAccessible", name));

        var reset = new HyperlinkButton { Content = Loc.Get("Settings_Reset"), Padding = new Thickness(0), FontSize = 12, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(reset, Loc.Format("Settings_ResetAccessible", name));

        var left = TitleBlock(Loc.Format("Settings_NameDash", name, what), reason);
        if (recommendedLabel is not null) left.Children.Add(Secondary(Loc.Format("Settings_RecommendedLine", recommendedLabel.ToLower(System.Globalization.CultureInfo.CurrentUICulture))));
        left.Children.Add(reset);
        var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
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
        var radios = new RadioButtons { MaxColumns = Math.Min(options.Length, 3) };
        AutomationProperties.SetName(radios, name);
        foreach (var o in options) radios.Items.Add(o);
        radios.SelectionChanged += (_, _) =>
        {
            if (_loading || radios.SelectedIndex < 0) return;
            set(radios.SelectedIndex);
        };
        _syncs.Add(() => radios.SelectedIndex = get());
        // Options can be long in some languages, so they sit under the label at full width instead of squeezing it.
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(TitleBlock(name, sub));
        content.Children.Add(radios);
        Root.Children.Add(new Border { Style = (Style)Resources["CardBorder"], Child = content });
    }

    private void AddSwitch(string name, string sub, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { OnContent = Loc.Get("Common_On"), OffContent = Loc.Get("Common_Off"), MinWidth = 0 };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) =>
        {
            if (!_loading) set(toggle.IsOn);
        };
        _syncs.Add(() => toggle.IsOn = get());
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        Root.Children.Add(Card(TitleBlock(name, sub), toggle));
    }

    private void AddWorkingHours()
    {
        var start = new TimePicker { Header = Loc.Get("Settings_Start"), ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        var end = new TimePicker { Header = Loc.Get("Settings_End"), ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        AutomationProperties.SetName(start, Loc.Get("Settings_DayStart"));
        AutomationProperties.SetName(end, Loc.Get("Settings_DayEnd"));
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        times.Children.Add(start);
        times.Children.Add(end);

        var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var buttons = new List<ToggleButton>();
        foreach (var d in Week)
        {
            var b = new ToggleButton { Content = System.Globalization.CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(d), MinWidth = 48 };
            AutomationProperties.SetName(b, System.Globalization.CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(d));
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
        content.Children.Add(TitleBlock(Loc.Get("Settings_Section_WorkingHours"), Loc.Get("Settings_WorkingHoursSub")));
        content.Children.Add(times);
        content.Children.Add(days);
        Root.Children.Add(new Border { Style = (Style)Resources["CardBorder"], Child = content });
    }
}
