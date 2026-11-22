using DeskPal.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeskPal;

// First-run setup: language, working hours, done. Choosing another language switches at once (no confirmation timer,
// the user is right here), saves, and restarts; the restarted copy opens on the hours page.
internal sealed class WelcomeWizardWindow : Window
{
    private const int Pages = 3;

    private readonly SettingsHub _hub;
    private readonly Action _finished;
    private readonly Grid _body = new();
    private readonly TextBlock _step = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly UIElement[] _pages;
    private int _page;
    private bool _closeHandled;

    public WelcomeWizardWindow(SettingsHub hub, string appliedLanguage, int startPage, Action finished, Action closed)
    {
        _hub = hub;
        _finished = finished;
        Title = Loc.Get("Wizard_WindowTitle");
        SystemBackdrop = new MicaBackdrop();

        _pages = [LanguagePage(appliedLanguage), HoursPage(), DonePage()];
        foreach (var p in _pages) _body.Children.Add(p);

        _back.Content = Loc.Get("Wizard_Back");
        _back.Click += (_, _) => Go(_page - 1);
        _next.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _next.MinWidth = 110;
        _next.Click += (_, _) =>
        {
            if (_page == Pages - 1) Finish();
            else Go(_page + 1);
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(_back);
        footer.Children.Add(_next);

        _step.Style = (Style)Application.Current.Resources["SecondaryTextStyle"];
        _step.FontSize = 12;

        var root = new Grid { Padding = new Thickness(28, 20, 28, 20), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_step);
        Grid.SetRow(_body, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(_body);
        root.Children.Add(footer);
        Content = root;
        Appearance.Register(this, root, Appearance.Role.Chrome);
        Keys.Wire(root, Close, () => Keys.Invoke(_next));
        Keys.Hint(_next, "Enter");

        AppWindow.Resize(new SizeInt32(560, 640));
        if (AppWindow.Presenter is OverlappedPresenter p2) p2.IsMaximizable = false;
        AppWindow.Closing += (_, _) =>
        {
            if (_closeHandled) return;
            _closeHandled = true;
            closed();
        };
        Go(Math.Clamp(startPage, 0, Pages - 1));
    }

    private void Go(int page)
    {
        _page = Math.Clamp(page, 0, Pages - 1);
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
        _step.Text = Loc.Format("Wizard_Step", _page + 1, Pages);
        _back.Visibility = _page == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = Loc.Get(_page == Pages - 1 ? "Wizard_Finish" : "Wizard_Next");
    }

    private void Finish()
    {
        _closeHandled = true;
        _finished();
        Close();
    }

    private static StackPanel PageShell(string titleKey, string? subKey)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Loc.Get(titleKey), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        if (subKey is not null)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get(subKey),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });
        return panel;
    }

    private UIElement LanguagePage(string appliedLanguage)
    {
        var panel = PageShell("Wizard_Language_Title", "Wizard_Language_Sub");
        var picker = new LanguagePicker(appliedLanguage) { MaxHeight = 400 };
        picker.Chosen += language =>
        {
            _closeHandled = true;
            _hub.ChangeLanguage?.Invoke(language.Tag, false);
        };
        panel.Children.Add(picker);
        return panel;
    }

    private UIElement HoursPage()
    {
        var panel = PageShell("Wizard_Hours_Title", "Wizard_Hours_Sub");
        var s = _hub.Settings;
        var start = new TimePicker { Header = Loc.Get("Settings_Start"), ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = s.WorkingHours.Start.ToTimeSpan() };
        var end = new TimePicker { Header = Loc.Get("Settings_End"), ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = s.WorkingHours.End.ToTimeSpan() };
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
        loading = false;
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        times.Children.Add(start);
        times.Children.Add(end);
        panel.Children.Add(times);
        return panel;
    }

    private UIElement DonePage()
    {
        var panel = PageShell("Wizard_Done_Title", "Wizard_Done_Body");
        panel.Children.Add(CrashConsentUi.Build(_hub));
        return panel;
    }
}
