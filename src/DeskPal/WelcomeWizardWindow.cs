using DeskPal.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskPal;

// First run, three steps: (1) hello and language, (2) your day, (3) try it. Bloop waves beside every step. Every choice is
// saved as it is made, so Skip keeps what was picked. Choosing another language switches at once (no confirmation timer, the
// user is right here), saves, and restarts; the restarted copy opens on the second step. The first-run flag is set only after
// the window has closed.
internal sealed class WelcomeWizardWindow : Window
{
    private const int Pages = 3;
    private const int WidthDip = 720, HeightDip = 620;

    private readonly SettingsHub _hub;
    private readonly Action _finished;
    private readonly Action _tryStretch;
    private readonly Action _closedAction;
    private readonly Grid _host = new();
    private readonly Button _skip = new();
    private readonly Button _recommended = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly Grid _footer = new();
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border[] _dot = new Border[Pages];
    private readonly FrameworkElement[] _pages;
    private readonly WelcomeHero _hero;
    private readonly Grid _content = new();
    private int _page = -1;
    private bool _closeHandled;
    private bool _finishedClicked;
    private bool _stacked;
    private Action? _refreshDay;
    private readonly Border _crashStrip;

    public WelcomeWizardWindow(SettingsHub hub, string appliedLanguage, int startPage, Action finished, Action closed, Action tryStretch)
    {
        _hub = hub;
        _finished = finished;
        _closedAction = closed;
        _tryStretch = tryStretch;
        Title = Loc.Get("Wizard_WindowTitle");
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "deskpal.ico"));
        SystemBackdrop = new MicaBackdrop();

        _hero = new WelcomeHero(hub);
        _pages = [LanguagePage(appliedLanguage), DayPage(), TryPage()];
        foreach (var p in _pages)
        {
            p.Visibility = Visibility.Collapsed;
            _host.Children.Add(p);
        }

        _skip.Content = Loc.Get("Wizard_Skip");
        _skip.Click += (_, _) => Close();
        _recommended.Content = Loc.Get("Wizard_Recommended");
        _recommended.Click += (_, _) => TickGuard.Run("wizard-recommended", ApplyRecommended);
        _back.Content = Loc.Get("Wizard_Back");
        _back.Click += (_, _) => Go(_page - 1);
        _next.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _next.MinWidth = 110;
        _next.Click += (_, _) =>
        {
            if (_page == Pages - 1) Finish();
            else Go(_page + 1);
        };
        foreach (var b in new[] { _skip, _recommended, _back, _next }) b.MinHeight = 32;
        _left.Children.Add(_skip);
        _left.Children.Add(_recommended);
        _right.Children.Add(_back);
        _right.Children.Add(_next);
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _footer.RowSpacing = 8;
        Grid.SetColumn(_right, 1);
        _footer.Children.Add(_left);
        _footer.Children.Add(_right);
        _footer.SizeChanged += (_, _) => FitFooter();

        for (var i = 0; i < Pages; i++)
        {
            _dot[i] = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Height='8' Width='8' CornerRadius='4' Background='{ThemeResource AccentFillColorDefaultBrush}' />");
            _dots.Children.Add(_dot[i]);
        }
        AutomationProperties.SetAccessibilityView(_dots, AccessibilityView.Raw);

        // Left column: Bloop with the step dots under him. Right column: the current step.
        var heroColumn = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Width = WelcomeHero.Size };
        heroColumn.Children.Add(_hero);
        _dots.Margin = new Thickness(0, -66, 0, 0);
        heroColumn.Children.Add(_dots);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _host,
            Padding = new Thickness(0, 0, 12, 0),
        };
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WelcomeHero.Size) });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _content.ColumnSpacing = 24;
        _content.Children.Add(heroColumn);
        Grid.SetColumn(scroll, 1);
        _content.Children.Add(scroll);

        // The crash-report option lives in its own strip above the buttons, on the last step only.
        _crashStrip = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' CornerRadius='8' Padding='12' BorderThickness='1' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' />");
        _crashStrip.Child = CrashConsentUi.Build(hub);
        _crashStrip.Visibility = Visibility.Collapsed;
        var root = new Grid { Padding = new Thickness(32, 24, 32, 24), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_content);
        Grid.SetRow(_crashStrip, 1);
        root.Children.Add(_crashStrip);
        Grid.SetRow(_footer, 2);
        root.Children.Add(_footer);
        Content = root;
        _hero.Overlay = root;
        Appearance.Register(this, root, Appearance.Role.Chrome);
#if DEBUG
        if (AppData.DebugWizardHc) DebugHighContrast(root);
#endif
        // Esc is Skip, Enter is Next (a focused button or list keeps its own Enter).
        Keys.Wire(root, Close, () => Keys.Invoke(_next));
        Keys.Hint(_next, "Enter");
        Keys.Hint(_skip, "Esc");

        PlaceWindow();
        if (AppWindow.Presenter is OverlappedPresenter p2) { p2.IsMaximizable = false; p2.IsResizable = false; }
        // The flag and the main window come after the window has really closed.
        Closed += (_, _) =>
        {
            _hero.Stop();
            if (_closeHandled) return;
            _closeHandled = true;
            if (_finishedClicked) _finished();
            else _closedAction();
        };
        root.Loaded += (_, _) =>
        {
            FitFooter();
            _next.Focus(FocusState.Programmatic);
        };
        Go(Math.Clamp(startPage, 0, Pages - 1));
    }

#if DEBUG
    // Stand-in for a Windows contrast theme when it cannot be switched on: black window, white text, cyan highlights.
    private void DebugHighContrast(FrameworkElement root)
    {
        static SolidColorBrush B(string rgb) => new(Windows.UI.Color.FromArgb(255, Convert.ToByte(rgb[..2], 16), Convert.ToByte(rgb[2..4], 16), Convert.ToByte(rgb[4..], 16)));
        var white = B("FFFFFF"); var black = B("000000"); var cyan = B("1AEBFF"); var green = B("3FF23F");
        var r = root.Resources;
        foreach (var k in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "TextFillColorDisabledBrush" }) r[k] = white;
        foreach (var k in new[] { "SolidBackgroundFillColorBaseBrush", "LayerFillColorDefaultBrush", "CardBackgroundFillColorDefaultBrush", "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlFillColorTertiaryBrush", "ComboBoxBackground", "TimePickerFlyoutButtonBackground", "ButtonBackground", "ListViewItemBackground" }) r[k] = black;
        foreach (var k in new[] { "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "CardStrokeColorDefaultBrush", "ButtonBorderBrush", "ComboBoxBorderBrush", "TimePickerFlyoutButtonBorderBrush", "CheckBoxCheckBackgroundStrokeUnchecked" }) r[k] = white;
        foreach (var k in new[] { "ButtonForeground", "ComboBoxForeground", "CheckBoxForegroundUnchecked", "TimePickerFlyoutButtonForeground", "ListViewItemForeground" }) r[k] = white;
        foreach (var k in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" }) r[k] = cyan;
        foreach (var k in new[] { "TextOnAccentFillColorPrimaryBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" }) r[k] = black;
        r["AccentButtonBorderBrush"] = white;
        r["ControlStrongStrokeColorDefaultBrush"] = white;
        r["SolidBackgroundFillColorSecondaryBrush"] = black;
        r["AccentTextFillColorPrimaryBrush"] = green;
        r["HyperlinkButtonForeground"] = green;
        r["ListViewItemBackgroundSelected"] = cyan;
        r["ListViewItemForegroundSelected"] = black;
        r["CheckBoxCheckBackgroundFillChecked"] = cyan;
        r["CheckBoxCheckGlyphForegroundChecked"] = black;
        foreach (var d in _dot) d.Background = cyan;
        root.RequestedTheme = ElementTheme.Dark;
    }
#endif

    // About 720 x 620 device-independent pixels, centred, never larger than the work area.
    private void PlaceWindow()
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(WidthDip * scale), work.Width);
        var h = Math.Min((int)(HeightDip * scale), work.Height);
        var x = work.X + (work.Width - w) / 2;
        var y = work.Y + (work.Height - h) / 2;
        // UI checks only: DESKPAL_DEBUG_WINPOS=x,y keeps the test window off the owner's screen.
        if (AppData.IsDebugRun && Environment.GetEnvironmentVariable("DESKPAL_DEBUG_WINPOS")?.Split(',') is [var px, var py]
            && int.TryParse(px, out var dx) && int.TryParse(py, out var dy)) { x = dx; y = dy; }
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // With big text the left and right button groups no longer fit on one line; the right group drops under the left one.
    private void FitFooter()
    {
        var width = _footer.ActualWidth;
        if (width <= 0) return;
        _left.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _right.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var stack = _left.DesiredSize.Width + _right.DesiredSize.Width + 16 > width;
        if (stack == _stacked) return;
        _stacked = stack;
        Grid.SetColumn(_right, stack ? 0 : 1);
        Grid.SetColumnSpan(_right, stack ? 2 : 1);
        Grid.SetRow(_right, stack ? 1 : 0);
    }

    private void Go(int page)
    {
        var target = Math.Clamp(page, 0, Pages - 1);
        if (target == _page) return;
        var previous = _page;
        _page = target;
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = _page == 0 ? Visibility.Collapsed : Visibility.Visible;
        _crashStrip.Visibility = _page == Pages - 1 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = Loc.Get(_page == Pages - 1 ? "Wizard_Finish" : "Wizard_Next");
        for (var i = 0; i < Pages; i++)
        {
            // The current step is a longer pill, so it is not told apart by shade alone.
            _dot[i].Width = i == _page ? 24 : 8;
            _dot[i].Opacity = i == _page ? 1 : 0.35;
        }
        AutomationProperties.SetName(_host, Loc.Format("Wizard_Step", _page + 1, Pages));
        FitFooter();
        if (previous < 0)
        {
            DebugPoke();
            return;
        }
        AnimateIn(_pages[_page], _page > previous ? 1 : -1);
        if (_page == 0) _hero.Wave();
        _next.Focus(FocusState.Programmatic);
    }

    // The new step fades in while it slides the last few pixels into place; instant when animation is off.
    private static void AnimateIn(FrameworkElement page, int direction)
    {
        if (!Motion.Enabled)
        {
            page.Opacity = 1;
            page.RenderTransform = null;
            return;
        }
        var shift = new TranslateTransform { X = 24 * direction };
        page.RenderTransform = shift;
        page.Opacity = 0;
        var d = new Duration(Motion.Ms(250));
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = d };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { From = shift.X, To = 0, Duration = d, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, "X");
        var board = new Storyboard();
        board.Children.Add(fade);
        board.Children.Add(slide);
        board.Begin();
    }

    // --debug-poke on the last step: Bloop is poked once through the button's own path, for UI checks.
    private void DebugPoke()
    {
        if (!AppData.DebugPoke || _page != Pages - 1) return;
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromSeconds(5);
        t.Tick += (_, _) => TickGuard.Run("wizard-debug-poke", _hero.Poke);
        t.Start();
    }

    private void Finish()
    {
        _finishedClicked = true;
        Close();
    }

    // The recommended hours and rhythm, saved now, then straight to the last step (or, from there, finished).
    private void ApplyRecommended()
    {
        var s = _hub.Settings;
        s.ResetRemindersToRecommended();
        var defaults = new WorkingHours();
        s.WorkingHours.Start = defaults.Start;
        s.WorkingHours.End = defaults.End;
        _hub.Commit(true);
        _refreshDay?.Invoke();
        // On the last step this is the end of setup; the crash-report tick stays as it is.
        if (_page == Pages - 1) Finish();
        else Go(Pages - 1);
    }

    private static StackPanel PageShell(string titleKey, string? subKey)
    {
        var panel = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0) };
        var title = new TextBlock { Text = Loc.Get(titleKey), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap, };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        panel.Children.Add(title);
        if (subKey is not null)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get(subKey),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });
        return panel;
    }

    private FrameworkElement LanguagePage(string appliedLanguage)
    {
        var panel = PageShell("Wizard_Hello_Title", "Wizard_Language_Sub");
        // Five whole rows (nothing cut mid-row) and a scroll bar that stays visible, so it reads as a list that goes on.
        var picker = new LanguagePicker(appliedLanguage) { MaxHeight = 262, Margin = new Thickness(-4, 0, 0, 0) };
        // A small chevron under the list says there are more rows; it goes away at the end of the list.
        var more = (FontIcon)XamlReader.Load("<FontIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Glyph='&#xE70D;' FontSize='12' HorizontalAlignment='Center' Foreground='{ThemeResource TextFillColorSecondaryBrush}' />");
        AutomationProperties.SetAccessibilityView(more, AccessibilityView.Raw);
        picker.Loaded += (_, _) =>
        {
            if (FindScroller(picker) is not { } inner) return;
            if (inner.FindName("VerticalScrollBar") is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar bar)
                bar.IndicatorMode = Microsoft.UI.Xaml.Controls.Primitives.ScrollingIndicatorMode.MouseIndicator;
            void Update() => more.Opacity = inner.ScrollableHeight - inner.VerticalOffset > 4 ? 1 : 0;
            inner.ViewChanged += (_, _) => Update();
            Update();
        };
        picker.Chosen += language =>
        {
            _closeHandled = true;
            _hub.ChangeLanguage?.Invoke(language.Tag, false);
        };
        panel.Children.Add(picker);
        panel.Children.Add(more);
        return panel;
    }

    private static ScrollViewer? FindScroller(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScroller(c) is { } found) return found;
        }
        return null;
    }

    private FrameworkElement DayPage()
    {
        var panel = PageShell("Wizard_Hours_Title", "Wizard_Hours_Sub");
        var s = _hub.Settings;
        var start = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = s.WorkingHours.Start.ToTimeSpan() };
        var end = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = s.WorkingHours.End.ToTimeSpan() };
        AutomationProperties.SetName(start, Loc.Get("Settings_DayStart"));
        AutomationProperties.SetName(end, Loc.Get("Settings_DayEnd"));
        var loading = true;
        void Changed()
        {
            if (loading) return;
            var a = TimeOnly.FromTimeSpan(start.Time);
            var b = TimeOnly.FromTimeSpan(end.Time);
            if (b > a)
            {
                s.WorkingHours.Start = a;
                s.WorkingHours.End = b;
                _hub.Commit(true);
            }
            else
            {
                loading = true;
                start.Time = s.WorkingHours.Start.ToTimeSpan();
                end.Time = s.WorkingHours.End.ToTimeSpan();
                loading = false;
            }
        }
        start.TimeChanged += (_, _) => Changed();
        end.TimeChanged += (_, _) => Changed();
        // Stacked: a time picker has a fixed minimum width, two side by side would not fit beside Bloop.
        var times = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        times.Children.Add(Labelled("Settings_Start", start));
        times.Children.Add(Labelled("Settings_End", end));
        panel.Children.Add(times);

        var rhythm = new ComboBox { Header = Loc.Get("Wizard_Rhythm_Label"), MinWidth = 260 };
        AutomationProperties.SetName(rhythm, Loc.Get("Wizard_Rhythm_Label"));
        var rhythmLoading = true;
        void FillRhythm()
        {
            rhythmLoading = true;
            rhythm.Items.Clear();
            var choices = DeskPalSettings.MovementChoices.Append(s.MovementMinutes).Distinct().Order().ToArray();
            foreach (var m in choices)
                rhythm.Items.Add(new ComboBoxItem
                {
                    Tag = m,
                    Content = Loc.Format(m == DeskPalSettings.RecommendedMovementMinutes ? "Wizard_Rhythm_Recommended" : "Wizard_Rhythm_Option", m),
                });
            rhythm.SelectedIndex = Array.IndexOf(choices, s.MovementMinutes);
            rhythmLoading = false;
        }
        FillRhythm();
        rhythm.SelectionChanged += (_, _) =>
        {
            if (rhythmLoading || rhythm.SelectedItem is not ComboBoxItem { Tag: int minutes }) return;
            s.MovementEnabled = true;
            s.StretchMinutes = minutes;
            _hub.Commit(true);
        };
        panel.Children.Add(rhythm);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Wizard_Rhythm_Note"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        });
        loading = false;
        _refreshDay = () =>
        {
            loading = true;
            start.Time = s.WorkingHours.Start.ToTimeSpan();
            end.Time = s.WorkingHours.End.ToTimeSpan();
            loading = false;
            FillRhythm();
        };
        return panel;
    }

    // A visible outline so the fields read as fields in every theme (the fill alone is dim in contrast themes).
    private static StackPanel Labelled(string labelKey, FrameworkElement field)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(new TextBlock { Text = Loc.Get(labelKey) });
        p.Children.Add(Outlined(field));
        return p;
    }

    private static Border Outlined(FrameworkElement field)
    {
        var b = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' CornerRadius='4' BorderThickness='1' HorizontalAlignment='Left' BorderBrush='{ThemeResource ControlStrongStrokeColorDefaultBrush}' />");
        b.Child = field;
        return b;
    }

    private FrameworkElement TryPage()
    {
        var panel = PageShell("Wizard_Try_Title", "Wizard_Try_Sub");
        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        var stretch = new Button { Content = Loc.Get("Wizard_Try_Stretch"), Style = accent };
        var poke = new Button { Content = Loc.Get("Wizard_Try_Poke"), Style = accent };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        stretch.Click += (_, _) => TickGuard.Run("wizard-try-stretch", () =>
        {
            _tryStretch();
            result.Text = Loc.Get("Wizard_Try_Sent");
            result.Visibility = Visibility.Visible;
        });
        poke.Click += (_, _) => TickGuard.Run("wizard-try-poke", _hero.Poke);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(stretch);
        buttons.Children.Add(poke);
        panel.Children.Add(buttons);
        panel.Children.Add(result);
        panel.Children.Add(InfoRow("", "Wizard_Try_Tray"));
        panel.Children.Add(InfoRow("", "Wizard_Try_Pro"));
        return panel;
    }

    private static Grid InfoRow(string glyph, string textKey)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Theme resource so the glyph follows light, dark and contrast themes of this window, not the app's.
        var icon = (FontIcon)XamlReader.Load("<FontIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontSize='16' VerticalAlignment='Top' Margin='0,2,0,0' Foreground='{ThemeResource TextFillColorSecondaryBrush}' />");
        icon.Glyph = glyph;
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var text = new TextBlock { Text = Loc.Get(textKey), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }
}
